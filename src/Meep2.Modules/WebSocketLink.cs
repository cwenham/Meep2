using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>A connection already has as many messages waiting to go as it can take, so another was dropped (Q153).</summary>
internal sealed class QueueFullException(string message) : IOException(message);

/// <summary>
/// One open WebSocket, either end: reads whole messages, and sends queued ones in order, so writes from several
/// branches queue up rather than interleave. The queue has a limit (Q153).
/// </summary>
internal sealed class WebSocketLink
{
    private static readonly TimeSpan s_closing = TimeSpan.FromSeconds(5);

    private readonly WebSocket _socket;
    private readonly Channel<Outgoing> _queue;
    private readonly Task _writer;
    private readonly TaskCompletionSource _readerDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ModuleContext? _owner;
    private long _lastActivity = Environment.TickCount64;
    private int _closing;

    /// <param name="socket">The WebSocket.</param>
    /// <param name="queueLimit">How many messages may wait to be sent.</param>
    /// <param name="owner">The module whose connection it is, which counts the bytes sent and received on it, whichever module sends them.</param>
    public WebSocketLink(WebSocket socket, int queueLimit, ModuleContext? owner = null)
    {
        _socket = socket;
        _owner = owner;
        _queue = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(queueLimit) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _writer = Task.Run(WriteAllAsync);
    }

    /// <summary>
    /// The counts for the other end, for a Listen's connection (<see cref="ModuleContext.Arrival"/>): what's sent on
    /// it, and replies dropped for a client too slow to keep up. Received messages are counted by whoever reads them.
    /// </summary>
    public Tally? Peer
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    /// <summary>When something was last sent or received, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
    public long LastActivity => Volatile.Read(ref _lastActivity);

    /// <summary>Whether it's open both ways, or at least for sending.</summary>
    public bool IsOpen => Volatile.Read(ref _closing) == 0 && _socket.State is WebSocketState.Open or WebSocketState.CloseReceived;

    /// <summary>The close code the other end sent, once it has.</summary>
    public int? CloseStatus => (int?)_socket.CloseStatus;

    /// <summary>Queues a message without waiting. False if the queue is full or the link is closing.</summary>
    public bool TryQueue(Frame frame) => IsOpen && _queue.Writer.TryWrite(new Outgoing(frame, null));

    /// <summary>Queues a message, waiting for room, and completes when it has been sent.</summary>
    public async Task SendAsync(Frame frame, CancellationToken cancellationToken)
    {
        if (!IsOpen)
            throw new IOException("the connection has closed");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _queue.Writer.WriteAsync(new Outgoing(frame, done), cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException exception)
        {
            throw new IOException("the connection has closed", exception);
        }
        await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads whole messages until the other end closes, or the connection fails. A message over
    /// <paramref name="limit"/> bytes throws <see cref="FramingException"/>.
    /// </summary>
    public async IAsyncEnumerable<Frame> ReceiveAsync(long limit, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(16 * 1024);
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer.GetMemory(16 * 1024), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    yield break;
                buffer.Advance(result.Count);
                _owner?.CountBytes(result.Count, 0);
                Volatile.Write(ref _lastActivity, Environment.TickCount64);
                if (buffer.WrittenCount > limit)
                    throw new FramingException($"sent a message longer than {limit} bytes, so its connection was closed");
                if (!result.EndOfMessage)
                    continue;
                yield return new Frame(buffer.WrittenSpan.ToArray(), result.MessageType);
                buffer.ResetWrittenCount();
            }
        }
        finally
        {
            _readerDone.TrySetResult();
        }
    }

    /// <summary>
    /// Sends what's queued, then closes with a code and reason, waiting a little for the other end to agree before
    /// giving up on it. Does nothing if it's already closing.
    /// </summary>
    public async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
            return;
        _queue.Writer.TryComplete();
        try
        {
            await _writer.WaitAsync(s_closing).ConfigureAwait(false);
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(s_closing);
                await _socket.CloseOutputAsync(status, reason, timeout.Token).ConfigureAwait(false);
            }
            if (_socket.State != WebSocketState.Closed)
                await _readerDone.Task.WaitAsync(s_closing).ConfigureAwait(false);
            // A reader that stopped early, at a message too long, leaves the rest of it and the other end's close
            // unread. Closing over unread data resets the connection, and the reset can reach the other end before
            // our close does, losing its code. So read on to their close, as the protocol asks.
            if (_socket.State == WebSocketState.CloseSent)
            {
                using var timeout = new CancellationTokenSource(s_closing);
                var discard = new byte[4096];
                while ((await _socket.ReceiveAsync(discard, timeout.Token).ConfigureAwait(false)).MessageType != WebSocketMessageType.Close)
                {
                }
            }
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            // The other end didn't answer in time, or has gone.
        }
        _socket.Abort();
        _socket.Dispose();
    }

    // Sends queued messages in order. After a failure, everything still queued fails too.
    private async Task WriteAllAsync()
    {
        Exception? failure = null;
        await foreach (var outgoing in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (failure is null)
            {
                try
                {
                    await _socket.SendAsync(outgoing.Frame.Bytes, outgoing.Frame.Type, endOfMessage: true, CancellationToken.None).ConfigureAwait(false);
                    _owner?.CountBytes(0, outgoing.Frame.Bytes.Length);
                    Peer?.Count(sent: 1, bytesSent: outgoing.Frame.Bytes.Length);
                    Volatile.Write(ref _lastActivity, Environment.TickCount64);
                    outgoing.Done?.TrySetResult();
                    continue;
                }
                catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
                {
                    failure = new IOException($"the connection failed: {exception.Message}", exception);
                }
            }
            outgoing.Done?.TrySetException(failure);
        }
    }

    private readonly record struct Outgoing(Frame Frame, TaskCompletionSource? Done);
}
