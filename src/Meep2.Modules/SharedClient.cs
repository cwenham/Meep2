using System.Net.Sockets;
using System.Net.WebSockets;
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
