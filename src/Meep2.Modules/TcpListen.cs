using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Listen's TCP inflection: a source that accepts TCP connections and emits each message that arrives on one as a
/// <see cref="TcpMessage"/> carrying a lease on its connection. In Meeplang: <c>Listen "tcp://0.0.0.0:7000"</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each line is a message, unless <c>end</c> gives the text that ends one, such as <c>end="\r\n.\r\n"</c>, or
/// <c>length</c> gives the size of every message, for a protocol whose messages are all the same length (Q97). Either
/// way, the connection stays open for more.
/// </para>
/// <para>
/// With <c>connections=true</c>, Listen also emits <see cref="TcpConnected"/> when a client connects and
/// <see cref="TcpDisconnected"/> when the connection ends.
/// </para>
/// <para>
/// A connection stays open while the client keeps it open, and after that until every message from it has left the
/// pipeline. <c>Respond final=true</c> closes it sooner. A message longer than <c>max-size</c> closes the connection.
/// </para>
/// </remarks>
[Verb("Listen", Inflection = "TCP")]
[Emits(typeof(TcpMessage))]
[Emits(typeof(TcpConnected), When = nameof(TcpListenParameters.Connections))]
[Emits(typeof(TcpDisconnected), When = nameof(TcpListenParameters.Connections))]
public sealed class TcpListen(TcpListenParameters parameters) : ASource, IChosenByParameters<TcpListenParameters>
{
    /// <summary>Takes tcp:// addresses with a port, and one way of telling where a message ends.</summary>
    public static string? WhyNot(TcpListenParameters parameters, Message message)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!Endpoints.TryTcp(parameters.Url, out _, out _))
            return $"needs a tcp:// address with a port, not {parameters.Url}";
        if (parameters.End is not null && parameters.Length is not null)
            return "takes end or length to tell where a message ends, not both";
        return null;
    }

    /// <inheritdoc/>
    protected override async Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        if (!Endpoints.TryTcp(parameters.Url, out var host, out var port))
            throw new InvalidOperationException($"Listen.TCP needs a tcp:// address with a port, not {parameters.Url}");
        var framing = Framing.For(parameters);
        var listener = new TcpListener(await Endpoints.ResolveAsync(host, cancellationToken).ConfigureAwait(false), port);
        listener.Start();
        var connections = new List<Task>();
        try
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                connections.RemoveAll(task => task.IsCompleted);
                connections.Add(Task.Run(() => ServeAsync(client, framing, context, cancellationToken), CancellationToken.None));
            }
            await Task.WhenAll(connections).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }

    // Emits each message from one connection, then waits until the connection's lease has closed.
    private async Task ServeAsync(TcpClient client, Framing framing, ModuleContext context, CancellationToken cancellationToken)
    {
        var lease = new TcpConnectionLease(client, framing.Ending);
        var emitted = false;
        var reason = "The client closed the connection.";
        using (lease.Hold())
        {
            if (parameters.Connections)
            {
                context.Emit(new TcpConnected { RemoteAddress = lease.Remote, Connection = lease.Number }, lease);
                emitted = true;
            }
            try
            {
                await foreach (var body in framing.ReadAsync(lease.Stream, parameters.MaxSize, cancellationToken).ConfigureAwait(false))
                {
                    context.Emit(new TcpMessage(Encoding.UTF8.GetString(body)) { Body = body, RemoteAddress = lease.Remote, Connection = lease.Number }, lease);
                    emitted = true;
                }
            }
            catch (FramingException exception)
            {
                reason = $"{lease.Remote} {exception.Message}.";
                context.EmitError(reason);
                await lease.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
            {
                reason = lease.IsClosed ? "The connection was closed from this end."
                    : cancellationToken.IsCancellationRequested ? "The pipeline stopped."
                    : $"The connection failed: {exception.Message}";
            }

            if (parameters.Connections)
                context.Emit(new TcpDisconnected { RemoteAddress = lease.Remote, Connection = lease.Number, Reason = reason }, lease);
        }
        if (!emitted)
            await lease.CloseAsync().ConfigureAwait(false);
        await lease.Closed.ConfigureAwait(false);
    }
}

/// <summary>Parameters for <see cref="TcpListen"/>.</summary>
public sealed record TcpListenParameters
{
    /// <summary>The address to listen at, such as <c>tcp://0.0.0.0:7000</c>. Host 0.0.0.0 listens on every network.</summary>
    [Positional]
    public required string Url { get; init; }

    /// <summary>
    /// The text that ends each message, such as <c>"\r\n.\r\n"</c>. It isn't part of the message, and the connection
    /// stays open for more. Without it, or <see cref="Length"/>, each line is a message.
    /// </summary>
    public string? End
    {
        get;
        init => field = value is null || value.Length > 0 ? value : throw new ArgumentException("The end of a message needs at least one character.", nameof(End));
    }

    /// <summary>The size of every message, in bytes, for a protocol whose messages are all the same length.</summary>
    public int? Length
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(Length), "A message's length must be at least 1 byte.");
    }

    /// <summary>Also emits <see cref="TcpConnected"/> and <see cref="TcpDisconnected"/> as clients come and go.</summary>
    public bool Connections { get; init; }

    /// <summary>The longest message to read, in bytes. 1 MiB unless set, or <see cref="Length"/> if that's longer.</summary>
    public long MaxSize
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxSize), "The size limit must be at least 1 byte.");
    } = 1024 * 1024;
}

/// <summary>A connection to <see cref="TcpListen"/>. Writes from several branches queue up rather than interleave.</summary>
internal sealed class TcpConnectionLease : Lease
{
    private readonly TcpClient _client;
    private readonly SemaphoreSlim _writing = new(1, 1);

    public TcpConnectionLease(TcpClient client, string ending)
    {
        _client = client;
        Ending = ending;
        Stream = client.GetStream();
        Remote = client.Client.RemoteEndPoint?.ToString() ?? "a client";
    }

    public NetworkStream Stream { get; }

    public string Remote { get; }

    /// <summary>What ends a message on this connection, which Respond writes after its own unless told otherwise.</summary>
    public string Ending { get; }

    /// <summary>Writes text and what ends it, then closes the connection if <paramref name="close"/> is set.</summary>
    public async Task WriteAsync(string text, string ending, bool close, CancellationToken cancellationToken)
    {
        if (IsClosed)
            throw new IOException($"the connection from {Remote} has closed");
        var bytes = Encoding.UTF8.GetBytes(string.Concat(text, ending));
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
        if (close)
            await CloseAsync().ConfigureAwait(false);
    }

    public override string ToString() => $"the connection from {Remote}";

    protected override ValueTask OnClosedAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A connection sent something Listen can't take as a message.</summary>
internal sealed class FramingException(string message) : Exception(message);

/// <summary>How Listen.TCP tells where one message ends and the next begins: lines, an end marker, or a fixed length.</summary>
internal abstract class Framing
{
    public static Framing For(TcpListenParameters parameters) =>
        parameters.Length is { } length ? new Fixed(length)
        : parameters.End is { } end ? new Marked(Encoding.UTF8.GetBytes(end), end)
        : new Lines();

    /// <summary>What ends a message, written after each of Respond's by default.</summary>
    public abstract string Ending { get; }

    /// <summary>Reads messages until the connection ends. A message without its end at the very end still counts.</summary>
    public async IAsyncEnumerable<byte[]> ReadAsync(Stream stream, long limit, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                while (TryRead(ref buffer, out var message))
                {
                    if (message.Length > Math.Max(limit, MinimumLimit))
                        throw new FramingException($"sent a message longer than {limit} bytes, so its connection was closed");
                    yield return message;
                }
                if (buffer.Length > Math.Max(limit, MinimumLimit))
                    throw new FramingException($"sent a message longer than {limit} bytes, so its connection was closed");
                if (result.IsCompleted)
                {
                    if (buffer.Length > 0)
                        yield return Last(buffer);
                    yield break;
                }
                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A message's size can always reach this, whatever max-size says.</summary>
    protected virtual long MinimumLimit => 0;

    /// <summary>Takes one whole message off the front of the buffer, if there is one.</summary>
    protected abstract bool TryRead(ref ReadOnlySequence<byte> buffer, out byte[] message);

    /// <summary>What's left when the connection ends: the last message, without its end.</summary>
    protected virtual byte[] Last(ReadOnlySequence<byte> buffer) => buffer.ToArray();

    // Each line is a message. A \r before the \n is dropped.
    private sealed class Lines : Framing
    {
        public override string Ending => "\n";

        protected override bool TryRead(ref ReadOnlySequence<byte> buffer, out byte[] message)
        {
            if (buffer.PositionOf((byte)'\n') is not { } end)
            {
                message = [];
                return false;
            }
            message = WithoutReturn(buffer.Slice(0, end));
            buffer = buffer.Slice(buffer.GetPosition(1, end));
            return true;
        }

        protected override byte[] Last(ReadOnlySequence<byte> buffer) => WithoutReturn(buffer);

        private static byte[] WithoutReturn(ReadOnlySequence<byte> line) =>
            line.Length > 0 && line.Slice(line.Length - 1).FirstSpan[0] == (byte)'\r' ? line.Slice(0, line.Length - 1).ToArray() : line.ToArray();
    }

    // Messages end with a marker, such as "\r\n.\r\n", which isn't part of them.
    private sealed class Marked(byte[] marker, string text) : Framing
    {
        public override string Ending => text;

        protected override bool TryRead(ref ReadOnlySequence<byte> buffer, out byte[] message)
        {
            var reader = new SequenceReader<byte>(buffer);
            if (!reader.TryReadTo(out ReadOnlySequence<byte> found, marker, advancePastDelimiter: true))
            {
                message = [];
                return false;
            }
            message = found.ToArray();
            buffer = buffer.Slice(reader.Position);
            return true;
        }
    }

    // Every message is the same number of bytes.
    private sealed class Fixed(int length) : Framing
    {
        public override string Ending => "";

        protected override long MinimumLimit => length;

        protected override bool TryRead(ref ReadOnlySequence<byte> buffer, out byte[] message)
        {
            if (buffer.Length < length)
            {
                message = [];
                return false;
            }
            message = buffer.Slice(0, length).ToArray();
            buffer = buffer.Slice(length);
            return true;
        }

        protected override byte[] Last(ReadOnlySequence<byte> buffer) =>
            throw new FramingException($"closed its connection {buffer.Length} bytes into a {length}-byte message, so that message was dropped");
    }
}
