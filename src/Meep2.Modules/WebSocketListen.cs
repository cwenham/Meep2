using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using AspRequest = Microsoft.AspNetCore.Http.HttpRequest;

namespace Meep2.Modules;

/// <summary>
/// Listen's WebSocket inflection: a source that accepts WebSocket connections and emits each message that arrives on
/// one as a <see cref="WebSocketText"/> or <see cref="WebSocketBinary"/>, carrying a lease on its connection. In
/// Meeplang: <c>Listen "ws://0.0.0.0:8080/chat/" as Room</c>, then <c>Send</c> to reply, or <c>Send "Room"</c> to send
/// to every connection.
/// </summary>
/// <remarks>
/// <para>
/// It runs on Kestrel, as Listen.HTTP does, and shares its server: a ws:// Listen and an http:// Listen can take the
/// same address, port and path, the WebSockets going to one and other requests to the other (Q149). A wss:// address
/// needs <c>certificate</c>, and <c>key</c> if the certificate's private key is in a file of its own.
/// </para>
/// <para>
/// Browsers send their cookies with a WebSocket whatever page opened it, so Listen only takes connections from pages
/// on its own host, unless <c>origins</c> names others, as in <c>origins="https://a.example, https://b.example"</c>, or
/// is <c>"*"</c> for any (Q151). Clients that aren't browsers don't say where they're from, and are always taken.
/// </para>
/// <para>
/// A connection stays open while the client keeps it open, and after that until every message from it has left the
/// pipeline. Listen pings each client every 30 seconds unless <c>ping</c> says otherwise, and closes connections that
/// stop answering. With <c>idle-timeout</c>, it also closes connections nothing has been sent or received on for that
/// long (Q154). A message longer than <c>max-size</c> closes its connection.
/// </para>
/// <para>
/// Each connection queues up to <c>max-queued</c> messages to send (256 unless set). When a client can't keep up, a
/// message that finds its queue full is dropped and logged (Q153), so one slow client can't hold up the rest.
/// </para>
/// <para>
/// With <c>connections=true</c>, Listen also emits <see cref="WebSocketConnected"/>, with the path, query and headers
/// the client connected with, and <see cref="WebSocketDisconnected"/> when the connection ends (Q150).
/// </para>
/// </remarks>
[Verb("Listen", Inflection = "WebSocket")]
[Emits(typeof(WebSocketText))]
[Emits(typeof(WebSocketBinary))]
[Emits(typeof(WebSocketConnected), When = nameof(WebSocketListenParameters.Connections))]
[Emits(typeof(WebSocketDisconnected), When = nameof(WebSocketListenParameters.Connections))]
public sealed class WebSocketListen(WebSocketListenParameters parameters) : ASource, IChosenByParameters<WebSocketListenParameters>, IReconfigurable<WebSocketListenParameters>
{
    private volatile WebSocketListenParameters _parameters = parameters;

    /// <summary>Takes ws:// and wss:// addresses, the latter with a certificate.</summary>
    public static string? WhyNot(WebSocketListenParameters parameters, Message message)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!HttpAddress.TryParse(parameters.Url, out var address, webSocket: true))
            return $"needs a ws:// or wss:// address, not {parameters.Url}";
        if (address.Secure && parameters.Certificate is null)
            return "needs a certificate for a wss:// address, as in certificate=\"server.pem\"";
        if (!address.Secure && (parameters.Certificate ?? parameters.Key) is not null)
            return "only uses a certificate with a wss:// address";
        if (parameters.Certificate is { } certificate && !File.Exists(certificate))
            return $"can't find the certificate {certificate}";
        if (parameters.Key is { } key && !File.Exists(key))
            return $"can't find the key {key}";
        return null;
    }

    /// <summary>
    /// Takes new parameters while it listens, for the connections that come after (Q121). A new address or certificate
    /// needs a Listen made afresh.
    /// </summary>
    public bool ParametersChanged(WebSocketListenParameters old, WebSocketListenParameters next)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(next);
        if (next.Url != old.Url || next.Certificate != old.Certificate || next.Key != old.Key)
            return false;
        _parameters = next;
        return true;
    }

    /// <summary>The name under which a Listen called <paramref name="name"/> shares its connections with Send.</summary>
    internal static string RoomKey(string name) => $"Listen.WebSocket:{name}";

    /// <inheritdoc/>
    protected override async Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        var start = _parameters;
        if (!HttpAddress.TryParse(start.Url, out var address, webSocket: true))
            throw new InvalidOperationException($"Listen.WebSocket needs a ws:// or wss:// address, not {start.Url}");
        var endpoint = new IPEndPoint(await Endpoints.ResolveAsync(address.Host, cancellationToken).ConfigureAwait(false), address.Port);
        var certificate = address.Secure ? Certificates.Load(start.Certificate!, start.Key) : null;

        var room = new Room();
        var key = context.Module.Name is { } name ? RoomKey(name) : null;
        if (key is not null)
            context.Shared[key] = room;
        var route = await HttpServer.AddAsync(endpoint, certificate, address.Path, http => AcceptAsync(http, room, context, cancellationToken), cancellationToken, webSocket: true)
            .ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        finally
        {
            // Connections finish the messages they have in the pipeline, then close.
            await route.DisposeAsync().ConfigureAwait(false);
            if (key is not null)
                context.Shared.TryRemove(new KeyValuePair<string, object>(key, room));
        }
    }

    // Takes one connection: emits each message from it, then waits until its lease has closed. Kestrel finishes the
    // request when this returns, so nothing touches the socket after that.
    private async Task AcceptAsync(HttpContext http, Room room, ModuleContext context, CancellationToken cancellationToken)
    {
        var parameters = _parameters;
        var request = http.Request;
        var url = request.GetDisplayUrl();
        if (WhyRefused(request, parameters.Origins) is { } refusal)
        {
            http.Response.StatusCode = 403;
            http.Response.ContentType = Bodies.PlainText;
            await http.Response.WriteAsync(refusal, CancellationToken.None).ConfigureAwait(false);
            var error = new ErrorMessage { Error = $"Listen refused a WebSocket to {url}: {refusal}" };
            context.Answered(error); // logged, but bad requests don't count towards stopping a server (Q100)
            context.Emit(error);
            return;
        }

        WebSocket socket;
        try
        {
            socket = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
            {
                KeepAliveInterval = parameters.Ping,
                KeepAliveTimeout = parameters.Ping > TimeSpan.Zero ? parameters.Ping : Timeout.InfiniteTimeSpan,
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or WebSocketException or InvalidOperationException)
        {
            return; // the client went away mid-handshake
        }

        var connection = http.Connection;
        var remote = connection.RemoteIpAddress is { } address ? new IPEndPoint(address, connection.RemotePort).ToString() : null;
        var lease = new WebSocketConnectionLease(new WebSocketLink(socket, parameters.MaxQueued), url, remote);
        room.Connections[lease.Number] = lease;
        try
        {
            await ServeAsync(lease, request, parameters, context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            room.Connections.TryRemove(lease.Number, out _);
        }
    }

    private static async Task ServeAsync(WebSocketConnectionLease lease, AspRequest request, WebSocketListenParameters parameters, ModuleContext context, CancellationToken cancellationToken)
    {
        var emitted = false;
        var reason = "The client closed the connection.";
        var hold = lease.Hold();

        // Stopping lets go of the connection, which closes once the messages it has in the pipeline have finished, or at
        // once if it hasn't sent any. It isn't read after that, so it can't send any more.
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using (cancellationToken.Register(() =>
        {
            lease.CloseWith(WebSocketCloseStatus.EndpointUnavailable, "The server is stopping.");
            hold.Dispose();
            if (!Volatile.Read(ref emitted))
                _ = lease.CloseAsync().AsTask();
        }).ConfigureAwait(false))
        {
            using var idle = parameters.IdleTimeout is { } timeout ? CloseWhenIdle(lease, timeout, reading.Token) : null;
            if (parameters.Connections)
            {
                context.Emit(
                    new WebSocketConnected
                    {
                        Url = lease.Url,
                        RemoteAddress = lease.Remote,
                        Connection = lease.Number,
                        Path = request.Path.Value ?? "/",
                        Query = Bodies.ToDictionary(request.Query),
                        Headers = Bodies.ToDictionary(request.Headers),
                    },
                    lease);
                emitted = true;
            }
            try
            {
                await foreach (var frame in lease.Link.ReceiveAsync(parameters.MaxSize, CancellationToken.None).ConfigureAwait(false))
                {
                    if (cancellationToken.IsCancellationRequested || lease.IsClosed)
                        continue; // read until the close, but take nothing more
                    context.Emit(Received(frame, lease.Url, lease.Remote, lease.Number), lease);
                    Volatile.Write(ref emitted, true);
                }
                if (lease.IsClosed)
                    reason = "The connection was closed from this end.";
            }
            catch (FramingException exception)
            {
                reason = $"{lease.Remote} {exception.Message}.";
                context.EmitError(reason);
                lease.CloseWith(WebSocketCloseStatus.MessageTooBig, $"Messages can be at most {parameters.MaxSize} bytes.");
                await lease.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
            {
                reason = lease.IsClosed ? "The connection was closed from this end."
                    : cancellationToken.IsCancellationRequested ? "The pipeline stopped."
                    : $"The connection failed: {exception.Message}";
            }
            reading.Cancel();

            if (parameters.Connections && !cancellationToken.IsCancellationRequested)
                context.Emit(new WebSocketDisconnected { Url = lease.Url, RemoteAddress = lease.Remote, Connection = lease.Number, Reason = reason, CloseStatus = lease.Link.CloseStatus }, lease);
        }
        hold.Dispose();
        if (!emitted)
            await lease.CloseAsync().ConfigureAwait(false);
        await lease.Closed.ConfigureAwait(false);
    }

    /// <summary>A message that arrived on a WebSocket, as Listen and Send emit it.</summary>
    internal static Message Received(Frame frame, string? url, string? remote, long connection) =>
        frame.Type == WebSocketMessageType.Text
            ? new WebSocketText(Encoding.UTF8.GetString(frame.Bytes.Span)) { Url = url, RemoteAddress = remote, Connection = connection }
            : new WebSocketBinary { Body = frame.Bytes, Url = url, RemoteAddress = remote, Connection = connection };

    // Closes the connection once nothing has been sent or received on it for the timeout.
    private static IDisposable CloseWhenIdle(WebSocketConnectionLease lease, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timer = new Timer(_ =>
        {
            if (cancellationToken.IsCancellationRequested || lease.IsClosed)
                return;
            if (Environment.TickCount64 - lease.Link.LastActivity < (long)timeout.TotalMilliseconds)
                return;
            lease.CloseWith(WebSocketCloseStatus.NormalClosure, "The connection was idle.");
            _ = lease.CloseAsync().AsTask();
        });
        var period = TimeSpan.FromMilliseconds(Math.Clamp(timeout.TotalMilliseconds / 4, 50, 5000));
        timer.Change(period, period);
        return timer;
    }

    // Why a connection from a browser page is refused, or null to take it: its page must be on Listen's own host, or
    // one that origins names (Q151).
    private static string? WhyRefused(AspRequest request, string? origins)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0 || origins?.Trim() == "*")
            return null;
        if (origins is not null)
        {
            var allowed = origins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return allowed.Any(a => string.Equals(a.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                ? null
                : $"pages from {origin} may not connect here.";
        }
        return Uri.TryCreate(origin, UriKind.Absolute, out var from) && string.Equals(from.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"pages from {origin} may not connect here, only pages from {request.Host.Value}. Listen's origins parameter can allow others.";
    }

    /// <summary>A Listen's open connections, which Send reaches by the Listen's name.</summary>
    internal sealed class Room
    {
        public ConcurrentDictionary<long, WebSocketConnectionLease> Connections { get; } = new();
    }
}

/// <summary>Parameters for <see cref="WebSocketListen"/>.</summary>
public sealed record WebSocketListenParameters
{
    /// <summary>
    /// The address to listen at, such as <c>ws://0.0.0.0:8080/chat/</c>. Host 0.0.0.0 listens on every network, and the
    /// path says which connections are this Listen's: those to it and below it.
    /// </summary>
    [Positional]
    public required string Url { get; init; }

    /// <summary>The certificate for a wss:// address: a PEM file, or a PKCS#12 file (.pfx or .p12) without a password.</summary>
    public string? Certificate { get; init; }

    /// <summary>The certificate's private key, as a PEM file, when it isn't in the certificate's own file.</summary>
    public string? Key { get; init; }

    /// <summary>
    /// The pages that may connect, besides those on Listen's own host: origins separated by commas, such as
    /// <c>"https://a.example, https://b.example"</c>, or <c>"*"</c> for any (Q151).
    /// </summary>
    public string? Origins { get; init; }

    /// <summary>Also emits <see cref="WebSocketConnected"/> and <see cref="WebSocketDisconnected"/> as clients come and go.</summary>
    public bool Connections { get; init; }

    /// <summary>The longest message to read, in bytes. 1 MiB unless set.</summary>
    public long MaxSize
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxSize), "The size limit must be at least 1 byte.");
    } = 1024 * 1024;

    /// <summary>How often to ping each client, closing connections that stop answering. 30 seconds unless set; 0 turns it off.</summary>
    public TimeSpan Ping
    {
        get;
        init => field = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(Ping), "The ping can't be less than zero.");
    } = TimeSpan.FromSeconds(30);

    /// <summary>Closes a connection nothing has been sent or received on for this long. Off unless set.</summary>
    public TimeSpan? IdleTimeout
    {
        get;
        init => field = value is null || value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(IdleTimeout), "The idle timeout must be longer than zero.");
    }

    /// <summary>How many messages each connection queues to send before dropping more (Q153). 256 unless set.</summary>
    public int MaxQueued
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxQueued), "A connection must be able to queue at least 1 message.");
    } = 256;
}
