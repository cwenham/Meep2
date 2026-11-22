using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Meep2.Modules;

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
