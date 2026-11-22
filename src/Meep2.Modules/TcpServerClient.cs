using System.Net.Sockets;
using System.Text;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>A TCP connection Send opened to a server. Writes from several branches queue up rather than interleave.</summary>
internal sealed class TcpServerClient(string address, string host, int port, SendTcpParameters parameters)
    : SharedClient(Key(host, port), address, parameters.Tries, parameters.RetryDelay)
{
    public override string Endpoint { get; } = Peers.Endpoint(host, port);

    private readonly Framing _framing = Framing.For(parameters.End, parameters.Length);
    private readonly SemaphoreSlim _writing = new(1, 1);
    private volatile TcpClient? _client;

    public static string Key(string host, int port) => $"TCP {host}:{port}";

    protected override async Task OpenAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _client = client;
    }

    protected override async IAsyncEnumerable<object> ReadAsync()
    {
        var client = _client ?? throw new IOException("not connected");
        await foreach (var body in _framing.ReadAsync(client.GetStream(), parameters.MaxSize, CancellationToken.None).ConfigureAwait(false))
            yield return body;
    }

    protected override long SizeOf(object payload) => ((byte[])payload).Length;

    protected override async Task<long> WriteAsync(Message message, string? text, string? end, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new IOException("not connected");
        var bytes = Encoding.UTF8.GetBytes(string.Concat(text ?? message.ToText(), end ?? parameters.End ?? _framing.Ending));
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = client.GetStream();
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException exception)
        {
            throw new IOException("the connection has closed", exception);
        }
        finally
        {
            _writing.Release();
        }
        return bytes.Length;
    }

    protected override Task CloseConnectionAsync()
    {
        _client?.Dispose();
        return Task.CompletedTask;
    }

    protected override Message Incoming(object payload, long connection)
    {
        var body = (byte[])payload;
        return new TcpMessage(Encoding.UTF8.GetString(body)) { Body = body, RemoteAddress = Address, Connection = connection };
    }

    protected override Message Connected(long connection) => new TcpConnected { RemoteAddress = Address, Connection = connection };

    protected override Message Disconnected(long connection, string reason, int? status) =>
        new TcpDisconnected { RemoteAddress = Address, Connection = connection, Reason = reason };
}
