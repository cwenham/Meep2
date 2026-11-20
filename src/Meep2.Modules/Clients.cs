using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// A connection Send opened to a server, shared by every Send in the process that names the same address (Q156). It
/// opens when a message is first sent, reconnects when it drops, and closes once no Send uses it. What the server
/// sends goes to every Send using the connection, which each emits under itself (Q158).
/// </summary>
/// <remarks>
/// Connecting is tried a few times, a fixed delay apart, before giving up with an error (Q157): when a message is sent,
/// and in the background after the connection drops. The background attempts use the tries and delay of the Send that
/// first opened the connection.
/// </remarks>
internal abstract class SharedClient(string key, string address, int tries, TimeSpan retryDelay)
{
    private static readonly Dictionary<string, SharedClient> s_clients = new(StringComparer.Ordinal);
    private static readonly Lock s_registry = new();

    private readonly SemaphoreSlim _connecting = new(1, 1);
    private volatile Subscription[] _subscriptions = [];
    private volatile bool _up;
    private volatile bool _closedOnPurpose;
    private volatile bool _shutDown;

    /// <summary>The server's address, as Send was given it.</summary>
    public string Address => address;

    /// <summary>The host and port it connects to, as destinations are counted (<see cref="Peers.EndpointOf(Uri)"/>).</summary>
    public abstract string Endpoint { get; }

    /// <summary>The host ID of the Meep at the other end, once the connection has learned it is one.</summary>
    public string? MeepId { get; protected set; }

    /// <summary>
    /// Joins the connection to an address, making it if no Send uses it yet. Clients are told apart by
    /// <paramref name="clientKey"/>, which says what kind of connection it is and where to.
    /// </summary>
    public static TClient Join<TClient>(string clientKey, Func<TClient> create, Subscription subscription)
        where TClient : SharedClient
    {
        lock (s_registry)
        {
            if (!s_clients.TryGetValue(clientKey, out var client) || client is not TClient)
                s_clients[clientKey] = client = create();
            client._subscriptions = [.. client._subscriptions, subscription];
            subscription.Client = client;
            return (TClient)client;
        }
    }

    /// <summary>Leaves the connection, which closes once nothing uses it.</summary>
    public void Leave(Subscription subscription)
    {
        bool last;
        lock (s_registry)
        {
            _subscriptions = [.. _subscriptions.Where(s => s != subscription)];
            last = _subscriptions.Length == 0;
            if (last && s_clients.TryGetValue(key, out var current) && current == this)
                s_clients.Remove(key);
        }
        subscription.Release();
        if (last)
            _ = ShutDownAsync();
    }

    /// <summary>Sends a message, connecting first if need be, and completes once it's sent, with the bytes written.</summary>
    public async Task<long> SendAsync(Message message, string? text, string? end, int attempts, TimeSpan delay, CancellationToken cancellationToken)
    {
        await EnsureUpAsync(attempts, delay, cancellationToken).ConfigureAwait(false);
        return await WriteAsync(message, text, end, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A reply through a message's lease: sent as <see cref="SendAsync"/> sends, and with <paramref name="final"/>,
    /// the connection closes after it, until a message is next sent.
    /// </summary>
    public async Task<long> ReplyAsync(Message message, string? text, string? end, bool final, CancellationToken cancellationToken)
    {
        var sent = await SendAsync(message, text, end, tries, retryDelay, cancellationToken).ConfigureAwait(false);
        if (final)
        {
            _closedOnPurpose = true;
            await CloseConnectionAsync().ConfigureAwait(false);
        }
        return sent;
    }

    private async Task EnsureUpAsync(int attempts, TimeSpan delay, CancellationToken cancellationToken)
    {
        if (_up)
            return;
        await _connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_up)
                return;
            if (_shutDown)
                throw new IOException($"the connection to {address} has closed");
            Exception? last = null;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    await OpenAsync(cancellationToken).ConfigureAwait(false);
                    _closedOnPurpose = false;
                    _up = true;
                    _ = Task.Run(ReadAllAsync, CancellationToken.None);
                    foreach (var subscription in _subscriptions)
                    {
                        if (subscription.Connections)
                            subscription.Deliver(Connected(subscription.Lease.Number));
                    }
                    return;
                }
                catch (Exception exception) when (exception is IOException or SocketException or WebSocketException or HttpRequestException
                    || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    last = exception;
                    if (attempt < attempts)
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
            throw new IOException($"couldn't connect after {attempts} {(attempts == 1 ? "try" : "tries")}: {last?.Message}", last);
        }
        finally
        {
            _connecting.Release();
        }
    }

    // Passes on what the server sends until the connection ends, then reconnects unless it was closed on purpose.
    private async Task ReadAllAsync()
    {
        var reason = "The server closed the connection.";
        try
        {
            await foreach (var payload in ReadAsync().ConfigureAwait(false))
            {
                var size = SizeOf(payload);
                foreach (var subscription in _subscriptions)
                {
                    // Each Send using the connection emits what arrives, so each counts it.
                    subscription.Outlet.CountBytes(size, 0);
                    subscription.Outlet.Destination(Endpoint).Count(received: 1, bytesReceived: size);
                    subscription.Deliver(Incoming(payload, subscription.Lease.Number));
                }
            }
        }
        catch (FramingException exception)
        {
            reason = $"{address} {exception.Message}.";
        }
        catch (Exception exception) when (exception is IOException or SocketException or WebSocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            reason = _closedOnPurpose ? "The connection was closed from this end." : $"The connection failed: {exception.Message}";
        }
        if (_closedOnPurpose && reason == "The server closed the connection.")
            reason = "The connection was closed from this end.";
        _up = false;
        var status = CloseStatus;
        await CloseConnectionAsync().ConfigureAwait(false);
        foreach (var subscription in _subscriptions)
        {
            if (subscription.Connections)
                subscription.Deliver(Disconnected(subscription.Lease.Number, reason, status));
        }
        if (!_closedOnPurpose && !_shutDown && _subscriptions.Length > 0)
            _ = ReconnectAsync(reason);
    }

    private async Task ReconnectAsync(string reason)
    {
        try
        {
            await Task.Delay(retryDelay).ConfigureAwait(false);
            await EnsureUpAsync(tries, retryDelay, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            foreach (var subscription in _subscriptions)
                subscription.Outlet.EmitError($"The connection to {address} dropped ({reason.TrimEnd('.')}), and reconnecting failed: {exception.Message}. Send will try again with its next message.", exception);
        }
    }

    private async Task ShutDownAsync()
    {
        _shutDown = true;
        _closedOnPurpose = true;
        await _connecting.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_up)
                await CloseConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            _connecting.Release();
        }
    }

    /// <summary>Opens the connection.</summary>
    protected abstract Task OpenAsync(CancellationToken cancellationToken);

    /// <summary>Reads what the server sends until the connection ends.</summary>
    protected abstract IAsyncEnumerable<object> ReadAsync();

    /// <summary>Writes a message: <paramref name="text"/>, or the message itself. Returns the bytes written.</summary>
    protected abstract Task<long> WriteAsync(Message message, string? text, string? end, CancellationToken cancellationToken);

    /// <summary>The bytes of something the server sent.</summary>
    protected abstract long SizeOf(object payload);

    /// <summary>Closes the connection, if it's open.</summary>
    protected abstract Task CloseConnectionAsync();

    /// <summary>The close code the server gave, if the protocol has them.</summary>
    protected virtual int? CloseStatus => null;

    /// <summary>A message for something the server sent.</summary>
    protected abstract Message Incoming(object payload, long connection);

    /// <summary>The message for the connection opening.</summary>
    protected abstract Message Connected(long connection);

    /// <summary>The message for the connection ending.</summary>
    protected abstract Message Disconnected(long connection, string reason, int? status);
}

/// <summary>
/// One Send's use of a shared connection: where it emits what the server sends, and the lease those messages carry,
/// so a Send without an address can reply through it.
/// </summary>
internal sealed class Subscription
{
    private readonly IDisposable _hold;

    public Subscription(Outlet outlet, bool connections)
    {
        Outlet = outlet;
        Connections = connections;
        Lease = new ClientLease(this);
        _hold = Lease.Hold();
    }

    public Outlet Outlet { get; }

    public bool Connections { get; }

    public ClientLease Lease { get; }

    public SharedClient Client { get; set; } = null!;

    public void Deliver(Message message) => Outlet.Emit(message, Lease);

    /// <summary>Lets go of the lease, which closes once messages carrying it have left the pipeline.</summary>
    public void Release() => _hold.Dispose();
}

/// <summary>A lease on a connection Send opened, carried by what the server sends.</summary>
internal sealed class ClientLease(Subscription subscription) : Lease, IConnectionLease
{
    public async Task ReplyAsync(Message message, string? text, string? end, bool final, CancellationToken cancellationToken)
    {
        if (IsClosed)
            throw new IOException($"{this} has closed");
        // Counted for the Send whose connection it is, whichever module replies.
        var sent = await subscription.Client.ReplyAsync(message, text, end, final, cancellationToken).ConfigureAwait(false);
        subscription.Outlet.CountBytes(0, sent);
        subscription.Outlet.Destination(subscription.Client.Endpoint).Count(sent: 1, bytesSent: sent);
    }

    public override string ToString() => $"the connection to {subscription.Client.Address}";

    // The connection is shared, so closing one Send's lease leaves it open.
    protected override ValueTask OnClosedAsync() => ValueTask.CompletedTask;
}

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
internal sealed class WebSocketClient(Uri uri, SendWebSocketParameters parameters, string? hostId = null, string? origin = null)
    : SharedClient(Key(uri, origin), uri.OriginalString, parameters.Tries, parameters.RetryDelay)
{
    private volatile WebSocketLink? _link;

    /// <summary>The key of the connection to an address: shared, or for one Send's origin alone.</summary>
    public static string Key(Uri uri, string? origin = null) =>
        origin is null ? $"WebSocket {uri.AbsoluteUri}" : $"WebSocket {uri.AbsoluteUri} from {origin}";

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
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
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
