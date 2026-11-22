using System.Net.Sockets;
using System.Text;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>A connection to <see cref="TcpListen"/>. Writes from several branches queue up rather than interleave.</summary>
internal sealed class TcpConnectionLease : Lease, IConnectionLease
{
    private readonly TcpClient _client;
    private readonly SemaphoreSlim _writing = new(1, 1);

    /// <param name="client">The connection.</param>
    /// <param name="ending">What ends a message on it.</param>
    /// <param name="listen">The Listen it came to, which counts the bytes read and written on it, whichever module writes them.</param>
    public TcpConnectionLease(TcpClient client, string ending, ModuleContext listen)
    {
        _client = client;
        Ending = ending;
        // TCP carries no headers, so what arrives is counted by the client's address (Peers).
        var remote = client.Client.RemoteEndPoint as System.Net.IPEndPoint;
        var from = listen.Arrival(remote is null ? "unknown" : Peers.ClientOf(remote.Address));
        From = from;
        Stream = new CountingStream(client.GetStream(), (received, sent) =>
        {
            listen.CountBytes(received, sent);
            from.Count(bytesReceived: received, bytesSent: sent);
        });
        Remote = client.Client.RemoteEndPoint?.ToString() ?? "a client";
    }

    /// <summary>The counts for where the connection comes from.</summary>
    public Tally From { get; }

    public Stream Stream { get; }

    public string Remote { get; }

    /// <summary>What ends a message on this connection, which a reply writes after its own unless told otherwise.</summary>
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
            From.Count(sent: 1);
        }
        finally
        {
            _writing.Release();
        }
        if (close)
            await CloseAsync().ConfigureAwait(false);
    }

    public Task ReplyAsync(Message message, string? text, string? end, bool final, CancellationToken cancellationToken) =>
        WriteAsync(text ?? message.ToText(), end ?? Ending, final, cancellationToken);

    public override string ToString() => $"the connection from {Remote}";

    protected override ValueTask OnClosedAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
