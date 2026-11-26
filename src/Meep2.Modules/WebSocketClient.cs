using System.Net.WebSockets;
using Meep2.SDK.Security;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>A WebSocket Send opened to a server.</summary>
/// <remarks>
/// To a host known to be Meep, each Send module has a connection of its own, which gives its origin in the handshake
/// as <c>baggage</c> (Q216). Elsewhere a connection is shared by every Send using its address (Q156), and its
/// handshake asks whether the other end is Meep; if the answer says so, each Send moves to a connection of its own
/// with its next message.
/// </remarks>
/// <param name="uri">Where it connects.</param>
/// <param name="parameters">The parameters of the Send that opened it.</param>
/// <param name="hostId">The host ID of the Send that opened it, or null for none, which asks nothing.</param>
/// <param name="origin">For a Send's own connection to a Meep host, its origin; null for a shared connection.</param>
/// <param name="reach">The addresses it may connect to (Q313); a connection limited to some isn't shared with others.</param>
internal sealed class WebSocketClient(Uri uri, SendWebSocketParameters parameters, string? hostId = null, string? origin = null, NetworkReach reach = NetworkReach.Any)
    : SharedClient(Key(uri, origin, reach), uri.OriginalString, parameters.Tries, parameters.RetryDelay)
{
    private volatile WebSocketLink? _link;

    /// <summary>The key of the connection to an address: shared, or for one Send's origin alone.</summary>
    public static string Key(Uri uri, string? origin = null, NetworkReach reach = NetworkReach.Any) =>
        (origin is null ? $"WebSocket {uri.AbsoluteUri}" : $"WebSocket {uri.AbsoluteUri} from {origin}")
        + (reach == NetworkReach.Any ? "" : $" ({reach.Describe()} addresses only)");

    public override string Endpoint { get; } = Peers.EndpointOf(uri) ?? uri.Authority;

    protected override async Task OpenAsync(CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = parameters.Ping;
        if (parameters.Ping > TimeSpan.Zero)
            socket.Options.KeepAliveTimeout = parameters.Ping;
        var marked = MeepHosts.Marked.Nothing;
        if (hostId is not null)
        {
            socket.Options.CollectHttpResponseDetails = true;
            if (origin is not null)
            {
                socket.Options.SetRequestHeader("baggage", MeepHosts.BaggageOf(origin));
                marked = MeepHosts.Marked.Origin;
            }
            else
            {
                socket.Options.SetRequestHeader(Peers.HostHeader, Peers.Ask);
                marked = MeepHosts.Marked.Asked;
            }
        }
        try
        {
            if (reach == NetworkReach.Any)
            {
                await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await socket.ConnectAsync(uri, Reaching.Invoker(reach), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (Reaching.Refusal(exception) is { } denied)
        {
            socket.Dispose();
            throw denied;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        if (marked != MeepHosts.Marked.Nothing)
        {
            var answer = socket.HttpResponseHeaders is { } headers
                && headers.FirstOrDefault(h => h.Key.Equals(Peers.HostHeader, StringComparison.OrdinalIgnoreCase)).Value is { } values
                ? values.FirstOrDefault()
                : null;
            MeepId = MeepHosts.Answered(Endpoint, answer, marked);
            socket.HttpResponseHeaders = null;
        }
        _link = new WebSocketLink(socket, parameters.MaxQueued);
    }

    protected override async IAsyncEnumerable<object> ReadAsync()
    {
        var link = _link ?? throw new IOException("not connected");
        await foreach (var frame in link.ReceiveAsync(parameters.MaxSize, CancellationToken.None).ConfigureAwait(false))
            yield return frame;
    }

    protected override async Task<long> WriteAsync(Message message, string? text, string? end, CancellationToken cancellationToken)
    {
        var link = _link ?? throw new IOException("not connected");
        var frame = await Frames.ForAsync(message, text, cancellationToken).ConfigureAwait(false);
        await link.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        return frame.Bytes.Length;
    }

    protected override long SizeOf(object payload) => ((Frame)payload).Bytes.Length;

    protected override Task CloseConnectionAsync() =>
        _link?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Goodbye.") ?? Task.CompletedTask;

    protected override int? CloseStatus => _link?.CloseStatus;

    protected override Message Incoming(object payload, long connection) =>
        WebSocketListen.Received((Frame)payload, Address, null, connection);

    protected override Message Connected(long connection) => new WebSocketConnected { Url = Address, Connection = connection };

    protected override Message Disconnected(long connection, string reason, int? status) =>
        new WebSocketDisconnected { Url = Address, Connection = connection, Reason = reason, CloseStatus = status };
}
