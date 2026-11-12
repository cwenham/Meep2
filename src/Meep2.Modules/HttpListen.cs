using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using MediaType = Microsoft.Net.Http.Headers.MediaTypeHeaderValue;

namespace Meep2.Modules;

/// <summary>
/// Listen's HTTP inflection: a source that receives HTTP and HTTPS requests and emits each one as an
/// <see cref="HttpRequest"/> carrying a lease on its connection. In Meeplang: <c>Listen "http://0.0.0.0:8080/quote/"</c>,
/// then <c>Send</c> to reply.
/// </summary>
/// <remarks>
/// <para>
/// It runs on Kestrel, ASP.NET Core's server (Q96). Listens on the same address and port share one server, and each
/// takes the requests under its own path. An https:// address needs <c>certificate</c>, and <c>key</c> if the
/// certificate's private key is in a file of its own.
/// </para>
/// <para>
/// Bodies are read into memory, up to <c>max-size</c>; a larger one is refused with 413. With <c>stream=true</c> the
/// body is left unread for one module to read (Q51). A request that nothing answers gets the default reply once it
/// and every message made from it have left the pipeline: 500 unless <c>default-status</c> says otherwise (Q50).
/// </para>
/// <para>When the pipeline stops, Listen stops taking requests but keeps the requests it has open until they're answered.</para>
/// </remarks>
[Verb("Listen", Inflection = "HTTP")]
[Emits(typeof(HttpRequest))]
public sealed class HttpListen(HttpListenParameters parameters) : ASource, IChosenByParameters<HttpListenParameters>, IReconfigurable<HttpListenParameters>
{
    private readonly Lock _changing = new();
    private volatile HttpListenParameters _parameters = parameters;
    private Listening? _listening;

    /// <summary>Takes http:// and https:// addresses, the latter with a certificate.</summary>
    public static string? WhyNot(HttpListenParameters parameters, Message message)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!HttpAddress.TryParse(parameters.Url, out var address))
            return $"needs an http:// or https:// address, not {parameters.Url}";
        if (address.Secure && parameters.Certificate is null)
            return "needs a certificate for an https:// address, as in certificate=\"server.pem\"";
        if (!address.Secure && (parameters.Certificate ?? parameters.Key) is not null)
            return "only uses a certificate with an https:// address";
        if (parameters.Certificate is { } certificate && !File.Exists(certificate))
            return $"can't find the certificate {certificate}";
        if (parameters.Key is { } key && !File.Exists(key))
            return $"can't find the key {key}";
        return null;
    }

    /// <inheritdoc/>
    protected override async Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        var start = _parameters;
        if (!HttpAddress.TryParse(start.Url, out var address))
            throw new InvalidOperationException($"Listen.HTTP needs an http:// or https:// address, not {start.Url}");
        var endpoint = new IPEndPoint(await Endpoints.ResolveAsync(address.Host, cancellationToken).ConfigureAwait(false), address.Port);
        var certificate = address.Secure ? Certificates.Load(start.Certificate!, start.Key) : null;
        var route = await HttpServer.AddAsync(endpoint, certificate, address.Path, http => AcceptAsync(http, context, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        lock (_changing)
            _listening = new Listening(address, endpoint, route, context, cancellationToken);
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
            // Requests already in the pipeline still need answering, so the server keeps them until they're done.
            HttpServer.Route current;
            lock (_changing)
            {
                current = _listening!.Route;
                _listening = null;
            }
            await current.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes new parameters while it listens, keeping its port open (Q121). A new path is taken on the same server, and
    /// the old one is let go once the requests it has open are answered. A new host, port, scheme or certificate
    /// needs a Listen made afresh.
    /// </summary>
    public bool ParametersChanged(HttpListenParameters old, HttpListenParameters next)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(next);
        if (!HttpAddress.TryParse(next.Url, out var address) || next.Certificate != old.Certificate || next.Key != old.Key)
            return false;
        lock (_changing)
        {
            if (_listening is not { } listening)
            {
                // Not listening yet, or any more: it starts with the new parameters, if it starts at all.
                _parameters = next;
                return true;
            }
            if (address.Secure != listening.Address.Secure || address.Host != listening.Address.Host || address.Port != listening.Address.Port)
                return false;
            _parameters = next;
            if (address.Path == listening.Address.Path)
                return true;

            var certificate = address.Secure ? Certificates.Load(next.Certificate!, next.Key) : null;
            var route = HttpServer.AddAsync(listening.Endpoint, certificate, address.Path, http => AcceptAsync(http, listening.Context, listening.Stopping), listening.Stopping)
                .GetAwaiter().GetResult();
            var replaced = listening.Route;
            _listening = listening with { Address = address, Route = route };
            _ = replaced.DisposeAsync().AsTask();
            return true;
        }
    }

    // Where it's listening, and what for: kept so new parameters can move it to another path.
    private sealed record Listening(HttpAddress Address, IPEndPoint Endpoint, HttpServer.Route Route, ModuleContext Context, CancellationToken Stopping);

    // Reads a request and emits it, then waits until its lease has closed. Kestrel finishes the response when this
    // returns, so nothing touches the request after that.
    private async Task AcceptAsync(HttpContext http, ModuleContext context, CancellationToken cancellationToken)
    {
        var request = http.Request;
        var url = request.GetDisplayUrl();
        var parameters = _parameters;
        var lease = new HttpRequestLease(http, url, parameters.DefaultStatus, parameters.DefaultReply);
        try
        {
            ReadOnlyMemory<byte> body = default;
            var text = "";
            StreamedBody? streamed = null;
            if (parameters.Stream)
            {
                streamed = new StreamedBody(request.Body, "request body");
            }
            else
            {
                try
                {
                    body = await Bodies.ReadAsync(request.Body, request.ContentLength ?? -1, parameters.MaxSize, cancellationToken).ConfigureAwait(false);
                }
                catch (BodyTooLargeException)
                {
                    await lease.AnswerAsync(413, Bodies.PlainText, Encoding.UTF8.GetBytes("The request's body is larger than this server accepts."), null, CancellationToken.None).ConfigureAwait(false);
                    await lease.CloseAsync().ConfigureAwait(false);
                    context.EmitError($"A request to {url} sent more than {parameters.MaxSize} bytes, so it was refused with 413.");
                    return;
                }
                text = EncodingOf(request.ContentType).GetString(body.Span);
            }

            var connection = http.Connection;
            context.Emit(
                new HttpRequest
                {
                    Method = request.Method,
                    Url = url,
                    Path = request.Path.Value ?? "/",
                    Query = Bodies.ToDictionary(request.Query),
                    Headers = Bodies.ToDictionary(request.Headers),
                    ContentType = request.ContentType,
                    Body = body,
                    Text = text,
                    RemoteAddress = connection.RemoteIpAddress is { } remote ? new IPEndPoint(remote, connection.RemotePort).ToString() : null,
                    Streamed = streamed,
                },
                lease);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // The client went away, or the pipeline is stopping, before the request could go into the pipeline.
            await lease.CloseAsync().ConfigureAwait(false);
            return;
        }
        await lease.Closed.ConfigureAwait(false);
    }

    private static Encoding EncodingOf(string? contentType) =>
        contentType is not null && MediaType.TryParse(contentType, out var media) && media.Encoding is { } encoding ? encoding : Encoding.UTF8;
}

/// <summary>Parameters for <see cref="HttpListen"/>.</summary>
public sealed record HttpListenParameters
{
    /// <summary>
    /// The address to listen at, such as <c>http://0.0.0.0:8080/quote/</c>. Host 0.0.0.0 listens on every network, and
    /// the path says which requests are this Listen's: those to it and below it.
    /// </summary>
    [Positional]
    public required string Url { get; init; }

    /// <summary>The certificate for an https:// address: a PEM file, or a PKCS#12 file (.pfx or .p12) without a password.</summary>
    public string? Certificate { get; init; }

    /// <summary>The certificate's private key, as a PEM file, when it isn't in the certificate's own file.</summary>
    public string? Key { get; init; }

    /// <summary>The largest request body to read, in bytes. 16 MiB unless set.</summary>
    public long MaxSize
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxSize), "The size limit must be at least 1 byte.");
    } = 16 * 1024 * 1024;

    /// <summary>Leaves request bodies unread, for one module to read (Q51).</summary>
    public bool Stream { get; init; }

    /// <summary>The status of the reply sent to a request nothing answered (Q50). 500 unless set.</summary>
    public int DefaultStatus
    {
        get;
        init => field = value is >= 100 and <= 999 ? value : throw new ArgumentOutOfRangeException(nameof(DefaultStatus), "An HTTP status is from 100 to 999.");
    } = 500;

    /// <summary>The text of the reply sent to a request nothing answered.</summary>
    public string DefaultReply { get; init; } = "No module answered this request.";
}

/// <summary>A request from <see cref="HttpListen"/> waiting for its answer.</summary>
internal sealed class HttpRequestLease(HttpContext http, string url, int defaultStatus, string defaultReply) : Lease
{
    private readonly Lock _answering = new();
    private Task? _answer;
    private bool _abandoned;

    public string Url => url;

    /// <summary>Sends the response, from bytes or a stream. Returns false if the request has already been answered.</summary>
    public async Task<bool> AnswerAsync(int status, string? contentType, ReadOnlyMemory<byte> body, Stream? stream, CancellationToken cancellationToken)
    {
        Task answer;
        lock (_answering)
        {
            if (_abandoned || IsClosed)
                throw new IOException($"the request for {url} has closed");
            if (_answer is not null)
                return false;
            answer = _answer = WriteAsync(status, contentType, body, stream, cancellationToken);
        }
        await answer.ConfigureAwait(false);
        return true;
    }

    private async Task WriteAsync(int status, string? contentType, ReadOnlyMemory<byte> body, Stream? stream, CancellationToken cancellationToken)
    {
        var response = http.Response;
        response.StatusCode = status;
        if (contentType is not null)
            response.ContentType = contentType;
        if (stream is null)
        {
            response.ContentLength = body.Length;
            await response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await stream.CopyToAsync(response.Body, cancellationToken).ConfigureAwait(false);
        }
        await response.CompleteAsync().ConfigureAwait(false);
    }

    // Nothing answered: send the default reply before closing (Q50).
    protected override async ValueTask OnFinishedAsync()
    {
        try
        {
            await AnswerAsync(defaultStatus, Bodies.PlainText, Encoding.UTF8.GetBytes(defaultReply), null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Closed already, or the client went away.
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            // The client went away.
        }
        await CloseAsync().ConfigureAwait(false);
    }

    // Kestrel reuses the request's objects once Listen lets go of it, so an answer still being written is finished
    // first, and a request nobody answered is abandoned.
    protected override async ValueTask OnClosedAsync()
    {
        Task? answer;
        lock (_answering)
        {
            answer = _answer;
            _abandoned = answer is null;
        }
        if (answer is null)
        {
            http.Abort();
            return;
        }
        try
        {
            await answer.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // Whoever was answering has been told.
        }
    }
}

/// <summary>
/// Kestrel servers, one for each address and port, shared by the Listens that use it. Each Listen takes the requests
/// to its own path and below, as with <c>/claims/spam/</c> and <c>/claims/copyright/</c> on one port; the longest
/// path that fits wins.
/// </summary>
internal sealed class HttpServer
{
    private static readonly Dictionary<IPEndPoint, HttpServer> s_servers = [];
    private static readonly SemaphoreSlim s_changing = new(1, 1);
    private static readonly byte[] s_stopping = Encoding.UTF8.GetBytes("This server is stopping.");

    private readonly IPEndPoint _endpoint;
    private readonly X509Certificate2? _certificate;
    private readonly WebApplication _host;
    private volatile Route[] _routes = []; // longest path first, replaced whole

    // An empty host: Kestrel and nothing else, so no settings files, environment variables or logging of its own.
    private HttpServer(IPEndPoint endpoint, X509Certificate2? certificate)
    {
        _endpoint = endpoint;
        _certificate = certificate;
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        if (certificate is not null)
            builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = null; // Listen applies max-size itself
            options.Listen(endpoint, listen =>
            {
                if (certificate is not null)
                    listen.UseHttps(certificate);
            });
        });
        _host = builder.Build();
        _host.UseWebSockets();
        _host.Run(ProcessRequestAsync);
    }

    /// <summary>
    /// Starts taking requests to a path, on the server for the endpoint, which starts if it isn't running. Takes
    /// charge of the certificate. A path can have one Listen for WebSockets and one for other requests (Q149).
    /// </summary>
    public static async Task<Route> AddAsync(IPEndPoint endpoint, X509Certificate2? certificate, string path, Func<HttpContext, Task> handler, CancellationToken cancellationToken, bool webSocket = false)
    {
        await s_changing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (s_servers.TryGetValue(endpoint, out var server))
            {
                var same = server._certificate?.Thumbprint == certificate?.Thumbprint;
                certificate?.Dispose();
                if (!same)
                {
                    throw new InvalidOperationException(server._certificate is null
                        ? $"Another Listen already takes plain http:// or ws:// requests on {endpoint}, so this one can't take https:// or wss:// there."
                        : $"Another Listen already takes https:// or wss:// requests on {endpoint} with a different certificate, or none.");
                }
                if (server._routes.Any(r => r.IsWebSocket == webSocket && string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Another Listen already takes {(webSocket ? "WebSockets" : "requests")} to {path} on {endpoint}.");
                var added = new Route(server, path, handler, webSocket);
                server._routes = [.. server._routes.Append(added).OrderByDescending(r => r.Path.Length)];
                return added;
            }

            // A new server knows its first path before it starts, so nothing that arrives at once finds nothing there.
            server = new HttpServer(endpoint, certificate);
            var route = new Route(server, path, handler, webSocket);
            server._routes = [route];
            try
            {
                await server._host.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await server._host.DisposeAsync().ConfigureAwait(false);
                certificate?.Dispose();
                throw;
            }
            s_servers[endpoint] = server;
            return route;
        }
        finally
        {
            s_changing.Release();
        }
    }

    // Stops the server once no Listen uses it.
    private async Task RemoveAsync(Route route)
    {
        await s_changing.WaitAsync().ConfigureAwait(false);
        try
        {
            _routes = [.. _routes.Where(r => r != route)];
            if (_routes.Length > 0)
                return;
            s_servers.Remove(_endpoint);
            await _host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _host.DisposeAsync().ConfigureAwait(false);
            _certificate?.Dispose();
        }
        finally
        {
            s_changing.Release();
        }
    }

    // A WebSocket goes to the WebSocket Listen whose path fits best, or to an HTTP Listen if none fits. Any other
    // request goes to an HTTP Listen, and gets 426 if only a WebSocket Listen takes its path.
    private async Task ProcessRequestAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        var upgrade = context.WebSockets.IsWebSocketRequest;
        if (!upgrade && !_routes.Any(r => !r.IsWebSocket && r.Fits(path)) && _routes.Any(r => r.IsWebSocket && r.Fits(path)))
        {
            var refused = context.Response;
            refused.StatusCode = 426;
            refused.Headers.Upgrade = "websocket";
            refused.ContentType = Bodies.PlainText;
            var explained = Encoding.UTF8.GetBytes($"{path} only takes WebSocket connections.");
            refused.ContentLength = explained.Length;
            await refused.Body.WriteAsync(explained).ConfigureAwait(false);
            return;
        }
        var webSocket = upgrade && _routes.Any(r => r.IsWebSocket && r.Fits(path));
        foreach (var route in _routes)
        {
            if (route.IsWebSocket != webSocket || !route.Fits(path))
                continue;
            if (!route.TryEnter())
                break;
            try
            {
                await route.Handler(context).ConfigureAwait(false);
            }
            finally
            {
                route.Exit();
            }
            return;
        }

        // Nothing takes this path, or the Listen that did is stopping.
        var response = context.Response;
        var found = _routes.Any(r => r.IsWebSocket == webSocket && r.Fits(path));
        response.StatusCode = found ? 503 : 404;
        response.ContentType = Bodies.PlainText;
        var text = found ? s_stopping : Encoding.UTF8.GetBytes($"Nothing is listening at {path}.");
        response.ContentLength = text.Length;
        await response.Body.WriteAsync(text).ConfigureAwait(false);
    }

    /// <summary>A Listen's path on a server. Disposing it stops new requests, waits for open ones, then lets go.</summary>
    internal sealed class Route(HttpServer server, string path, Func<HttpContext, Task> handler, bool webSocket) : IAsyncDisposable
    {
        private readonly TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _open = 1; // held until disposed, so it can only reach zero once it's closing
        private int _disposed;

        public string Path => path;

        public bool IsWebSocket => webSocket;

        public Func<HttpContext, Task> Handler => handler;

        public bool Fits(string requested) =>
            requested.StartsWith(path, StringComparison.OrdinalIgnoreCase)
            || (requested.Length == path.Length - 1 && path.StartsWith(requested, StringComparison.OrdinalIgnoreCase));

        public bool TryEnter()
        {
            for (var open = Volatile.Read(ref _open); open > 0; open = Volatile.Read(ref _open))
            {
                if (Interlocked.CompareExchange(ref _open, open + 1, open) == open)
                    return true;
            }
            return false;
        }

        public void Exit()
        {
            if (Interlocked.Decrement(ref _open) == 0)
                _idle.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Exit();
            await _idle.Task.ConfigureAwait(false);
            await server.RemoveAsync(this).ConfigureAwait(false);
        }
    }
}

/// <summary>An http:// or https:// address Listen takes: where to listen, and the path whose requests are its own.</summary>
internal readonly record struct HttpAddress(bool Secure, string Host, int Port, string Path)
{
    public static bool TryParse(string address, out HttpAddress parsed, bool webSocket = false)
    {
        parsed = default;
        var (plain, secure) = webSocket ? (Uri.UriSchemeWs, Uri.UriSchemeWss) : (Uri.UriSchemeHttp, Uri.UriSchemeHttps);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != plain && uri.Scheme != secure))
            return false;
        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        parsed = new HttpAddress(uri.Scheme == secure, uri.Host.Trim('[', ']'), uri.Port, Uri.UnescapeDataString(path));
        return true;
    }
}

/// <summary>Loads certificates for https:// addresses.</summary>
internal static class Certificates
{
    public static X509Certificate2 Load(string certificate, string? key)
    {
        var path = System.IO.Path.GetFullPath(certificate);
        if (System.IO.Path.GetExtension(path).ToLowerInvariant() is ".pfx" or ".p12")
            return X509CertificateLoader.LoadPkcs12FromFile(path, password: null);

        // A key loaded from PEM lives only in memory, which TLS on macOS and Windows can't use, so it goes through
        // PKCS#12 first.
        using var pem = X509Certificate2.CreateFromPemFile(path, key is null ? null : System.IO.Path.GetFullPath(key));
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), password: null);
    }
}
