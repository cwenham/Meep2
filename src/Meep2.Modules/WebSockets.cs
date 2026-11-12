using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Meep2.SDK.Messages;
using Microsoft.Net.Http.Headers;

namespace Meep2.Modules;

/// <summary>
/// A connection a Send without an address can reply through: one to Listen.TCP or Listen.WebSocket, or one Send opened
/// itself. Listen.HTTP's requests are answered differently, so they aren't one of these.
/// </summary>
internal interface IConnectionLease
{
    /// <summary>
    /// Writes a reply: <paramref name="text"/>, or the message itself when it's null. <paramref name="end"/> replaces
    /// what ends a message on a TCP connection; <paramref name="final"/> closes the connection after writing.
    /// </summary>
    /// <exception cref="QueueFullException">The other end is too slow, and this reply was dropped.</exception>
    Task ReplyAsync(Message message, string? text, string? end, bool final, CancellationToken cancellationToken);
}

/// <summary>A connection already has as many messages waiting to go as it can take, so another was dropped (Q153).</summary>
internal sealed class QueueFullException(string message) : IOException(message);

/// <summary>One WebSocket message to send, whole.</summary>
internal readonly record struct Frame(ReadOnlyMemory<byte> Bytes, WebSocketMessageType Type)
{
    public static Frame Text(string text) => new(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text);
}

/// <summary>Turns messages into WebSocket messages, text or binary, by what they hold (Q160).</summary>
internal static class Frames
{
    private const long Limit = 16 * 1024 * 1024;

    /// <summary>
    /// Text that was given goes as text. Bytes, from a binary WebSocket message or a body whose media type isn't text,
    /// go as binary. Everything else goes as its text.
    /// </summary>
    public static async ValueTask<Frame> ForAsync(Message message, string? text, CancellationToken cancellationToken)
    {
        if (text is not null)
            return Frame.Text(text);
        switch (message)
        {
            case WebSocketBinary binary:
                return new Frame(binary.Body, WebSocketMessageType.Binary);
            case IHasBody body when IsText(body.ContentType) || (body.ContentType is null && message is IHasText):
                if (message is IHasText hasText && !body.IsStreamed)
                    return Frame.Text(hasText.Text);
                return Frame.Text(Encoding.UTF8.GetString((await BytesOfAsync(body, cancellationToken).ConfigureAwait(false)).Span));
            case IHasBody body:
                return new Frame(await BytesOfAsync(body, cancellationToken).ConfigureAwait(false), WebSocketMessageType.Binary);
            case IHasText plain:
                return Frame.Text(plain.Text);
            default:
                return Frame.Text(message.ToText());
        }
    }

    /// <summary>A message's bytes: its body if it has one, or its text as UTF-8.</summary>
    public static async ValueTask<ReadOnlyMemory<byte>> BytesOfAsync(IHasBody body, CancellationToken cancellationToken)
    {
        switch (body)
        {
            case HttpRequest { IsStreamed: false } request:
                return request.Body;
            case HttpResponse { IsStreamed: false } response:
                return response.Body;
            case WebSocketBinary binary:
                return binary.Body;
        }
        await using var stream = body.OpenBody();
        return await Bodies.ReadAsync(stream, -1, Limit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a media type is text of some kind, such as <c>text/plain</c> or <c>application/json</c>.</summary>
    public static bool IsText(string? contentType)
    {
        if (contentType is null || !MediaTypeHeaderValue.TryParse(contentType, out var media))
            return false;
        var type = media.MediaType.Value ?? "";
        return type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || type.EndsWith("/json", StringComparison.OrdinalIgnoreCase) || type.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || type.EndsWith("/xml", StringComparison.OrdinalIgnoreCase) || type.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)
            || type.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
            || type.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
            || media.Charset.HasValue;
    }
}

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
    private long _lastActivity = Environment.TickCount64;
    private int _closing;

    public WebSocketLink(WebSocket socket, int queueLimit)
    {
        _socket = socket;
        _queue = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(queueLimit) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _writer = Task.Run(WriteAllAsync);
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

/// <summary>A client's connection to <see cref="WebSocketListen"/>.</summary>
internal sealed class WebSocketConnectionLease(WebSocketLink link, string url, string? remote) : Lease, IConnectionLease
{
    private volatile CloseReason _closeWith = new(WebSocketCloseStatus.NormalClosure, "Goodbye.");

    public WebSocketLink Link => link;

    public string Url => url;

    public string? Remote => remote;

    /// <summary>The code and reason to close with, when it closes.</summary>
    public void CloseWith(WebSocketCloseStatus status, string reason) => _closeWith = new CloseReason(status, reason);

    /// <summary>Queues a message without waiting. A full queue drops it (Q153).</summary>
    public void Queue(Frame frame)
    {
        if (IsClosed || !link.IsOpen)
            throw new IOException($"{this} has closed");
        if (!link.TryQueue(frame))
            throw new QueueFullException($"{this} is too slow to keep up, so a message to it was dropped");
    }

    public async Task ReplyAsync(Message message, string? text, string? end, bool final, CancellationToken cancellationToken)
    {
        Queue(await Frames.ForAsync(message, text, cancellationToken).ConfigureAwait(false));
        if (final)
            await CloseAsync().ConfigureAwait(false);
    }

    public override string ToString() => $"the WebSocket connection from {remote ?? "a client"}";

    protected override async ValueTask OnClosedAsync()
    {
        var reason = _closeWith;
        await link.CloseAsync(reason.Status, reason.Description).ConfigureAwait(false);
    }

    private sealed record CloseReason(WebSocketCloseStatus Status, string Description);
}
