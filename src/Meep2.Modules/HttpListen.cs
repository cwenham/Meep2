using System.Net;
using System.Text;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Security;
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
/// <para>
/// With <c>signature=required</c>, a request without a signature and a credential (<c>Signature-Input</c>, <c>Signature</c>
/// and <c>Meep-Credential</c>) is refused with 401 before it reaches the pipeline (Q253, Q280). That only keeps
/// anonymous requests out: <c>Verify</c> still checks the signature and the credential.
/// </para>
/// </remarks>
[Verb("Listen", Inflection = "HTTP")]
[ChecksPermission(MeepPermissions.NetworkListen, "its host and port")]
[ChecksPermission(MeepPermissions.FilesRead, "its certificate and key")]
[Role(VerbRole.Inlet)]
[Emits(typeof(HttpRequest))]
public sealed class HttpListen(HttpListenParameters parameters) : ASource, IChosenByParameters<HttpListenParameters>, IReconfigurable<HttpListenParameters>
{
    private readonly Lock _changing = new();
    private volatile HttpListenParameters _parameters = parameters;
    private Listening? _listening;
    private HttpListen.LastOrigin? _lastOrigin;

    /// <summary>Takes http:// and https:// addresses, the latter with a certificate.</summary>
    public static string? WhyNot(HttpListenParameters parameters, Message message)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!HttpAddress.TryParse(parameters.Url, out var address))
            return $"needs an http:// or https:// address, not {parameters.Url}";
        return Certificates.WhyNot("an https://", address.Secure, parameters.Certificate, parameters.Key);
    }

    /// <inheritdoc/>
    protected override async Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        var start = _parameters;
        if (!HttpAddress.TryParse(start.Url, out var address))
            throw new InvalidOperationException($"Listen.HTTP needs an http:// or https:// address, not {start.Url}");
        var reach = context.DemandEndpoint(MeepPermissions.NetworkListen, null, address.Host, address.Port);
        var endpoint = new IPEndPoint(Listens.Within(await Endpoints.ResolveAsync(address.Host, cancellationToken).ConfigureAwait(false), address.Host, reach), address.Port);
        var certificate = address.Secure ? Certificates.Load(start.Certificate!, start.Key, context) : null;
        var route = await HttpServer.AddAsync(endpoint, certificate, address.Path, http => AcceptAsync(http, context, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        lock (_changing)
            _listening = new Listening(address, endpoint, route, context, cancellationToken);
        context.Listening(Where(address, endpoint));
        MeepHosts.Bound(endpoint.Port, context.HostId);
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
            MeepHosts.Unbound(endpoint.Port, context.HostId);
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
        Listening listening;
        lock (_changing)
        {
            if (_listening is not { } current)
            {
                // Not listening yet, or any more: it starts with the new parameters, if it starts at all.
                _parameters = next;
                return true;
            }
            if (address.Secure != current.Address.Secure || address.Host != current.Address.Host || address.Port != current.Address.Port)
                return false;
            _parameters = next;
            if (address.Path == current.Address.Path)
                return true;
            listening = current;
        }

        // The new path is added outside the lock (P1): adding it waits for any other Listen starting a server, and
        // nothing else should wait on this one meanwhile. IReconfigurable is synchronous, so it still waits here.
        var certificate = address.Secure ? Certificates.Load(next.Certificate!, next.Key) : null;
        var route = HttpServer.AddAsync(listening.Endpoint, certificate, address.Path, http => AcceptAsync(http, listening.Context, listening.Stopping), listening.Stopping)
            .GetAwaiter().GetResult();
        HttpServer.Route replaced;
        lock (_changing)
        {
            if (ReferenceEquals(_listening, listening))
            {
                replaced = listening.Route;
                _listening = listening with { Address = address, Route = route };
            }
            else
            {
                replaced = route; // it stopped, or moved, while the path was added
            }
        }
        _ = replaced.DisposeAsync().AsTask();
        if (!ReferenceEquals(replaced, route))
            listening.Context.Listening(Where(address, listening.Endpoint));
        return true;
    }

    // Where it's listening, as it tells the runtime: the address it's bound to, and its path.
    internal static string Where(HttpAddress address, IPEndPoint endpoint, bool webSocket = false) =>
        $"{(webSocket ? address.Secure ? "wss" : "ws" : address.Secure ? "https" : "http")}://{endpoint}{address.Path}";

    // Where it's listening, and what for: kept so new parameters can move it to another path.
    private sealed record Listening(HttpAddress Address, IPEndPoint Endpoint, HttpServer.Route Route, ModuleContext Context, CancellationToken Stopping);

    // Reads a request and emits it, then waits until its lease has closed. Kestrel finishes the response when this
    // returns, so nothing touches the request after that.
    // A request's body: read whole, with its text, or left to stream.
    private static async Task<ArrivedBody> ReadBodyAsync(Stream body, Microsoft.AspNetCore.Http.HttpRequest request, HttpListenParameters parameters, CancellationToken cancellationToken)
    {
        if (parameters.Stream)
            return new ArrivedBody(default, "", new StreamedBody(body, "request body"));
        var bytes = await Bodies.ReadAsync(body, request.ContentLength ?? -1, parameters.MaxSize, cancellationToken).ConfigureAwait(false);
        return new ArrivedBody(bytes, EncodingOf(request.ContentType).GetString(bytes.Span), null);
    }

    // The message a request is, with its body.
    private static HttpRequest MessageOf(HttpContext http, string url, ArrivedBody body)
    {
        var request = http.Request;
        var connection = http.Connection;
        return new HttpRequest
        {
            Method = request.Method,
            Url = url,
            Path = request.Path.Value ?? "/",
            Query = Bodies.ToDictionary(request.Query),
            Headers = Bodies.ToDictionary(request.Headers),
            ContentType = request.ContentType,
            Body = body.Bytes,
            Text = body.Text,
            RemoteAddress = connection.RemoteIpAddress is { } remote ? new IPEndPoint(remote, connection.RemotePort).ToString() : null,
            Streamed = body.Streamed,
        };
    }

    // A request's body as it goes into its message.
    private sealed record ArrivedBody(ReadOnlyMemory<byte> Bytes, string Text, StreamedBody? Streamed);

    private async Task AcceptAsync(HttpContext http, ModuleContext context, CancellationToken cancellationToken)
    {
        var request = http.Request;
        var url = request.GetDisplayUrl();
        var parameters = _parameters;
        var (tally, traceParent) = Arrived(http, context, ref _lastOrigin);
        var lease = new HttpRequestLease(http, url, parameters.DefaultStatus, parameters.DefaultReply, context, tally);
        var requestBody = new CountingStream(request.Body, (received, sent) =>
        {
            context.CountBytes(received, sent);
            tally.Count(bytesReceived: received, bytesSent: sent);
        });
        try
        {
            ArrivedBody body;
            try
            {
                body = await ReadBodyAsync(requestBody, request, parameters, cancellationToken).ConfigureAwait(false);
            }
            catch (BodyTooLargeException)
            {
                tally.Count(received: 1);
                await lease.AnswerAsync(413, Bodies.PlainText, Encoding.UTF8.GetBytes("The request's body is larger than this server accepts."), null, CancellationToken.None).ConfigureAwait(false);
                await lease.CloseAsync().ConfigureAwait(false);
                context.EmitError($"A request to {url} sent more than {parameters.MaxSize} bytes, so it was refused with 413.");
                return;
            }

            if (parameters.Signature == RequiredSignature.Required && MissingSignature(request) is { } missing)
            {
                tally.Count(received: 1);
                context.CountDenied();
                await lease.AnswerAsync(401, Bodies.PlainText, Encoding.UTF8.GetBytes(
                    $"This server needs requests signed with a Meep credential, and this one has no {missing}."), null, CancellationToken.None).ConfigureAwait(false);
                await lease.CloseAsync().ConfigureAwait(false);
                return;
            }

            var emitting = MessageOf(http, url, body);
            if (traceParent is not null)
                context.ContinueTrace(emitting, traceParent);
            tally.Count(received: 1);
            context.Emit(emitting, lease);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // The client went away, or the pipeline is stopping, before the request could go into the pipeline.
            await lease.CloseAsync().ConfigureAwait(false);
            return;
        }
        await lease.Closed.ConfigureAwait(false);
    }

    /// <summary>The headers that prove who sent a request that a request lacks, in words, or null if it has them all.</summary>
    internal static string? MissingSignature(Microsoft.AspNetCore.Http.HttpRequest request)
    {
        var missing = s_signatureHeaders.Where(header => !request.Headers.TryGetValue(header, out var value) || string.IsNullOrWhiteSpace(value)).ToList();
        return missing.Count switch
        {
            0 => null,
            1 => missing[0],
            _ => $"{string.Join(", ", missing[..^1])} or {missing[^1]}",
        };
    }

    private static readonly string[] s_signatureHeaders = ["Signature-Input", "Signature", "Meep-Credential"];

    /// <summary>
    /// Where a request or WebSocket comes from, for counting it (<see cref="ModuleContext.Arrival"/>): the origin
    /// another Meep host gave, or else the client's address. Answers another Meep with this host's ID, if it's on this
    /// machine or a private address or the setting allows anyone, and returns the trace it continues, if any.
    /// </summary>
    internal static (Tally Tally, string? TraceParent) Arrived(HttpContext http, ModuleContext context, ref LastOrigin? last)
    {
        var headers = http.Request.Headers;
        var traceParent = headers.TryGetValue("traceparent", out var traced) && traced.Count > 0 ? traced.ToString() : null;
        var baggage = headers.TryGetValue("baggage", out var carried) ? carried.ToString() : "";
        // Most requests come from the same few origins: the last one's counts are kept, to skip reading it again.
        // This host's ID goes only to clients on this machine and private addresses, unless the setting says anyone (S11).
        var mayAnswer = context.HostId is not null && MeepHosts.MayAnswer(http.Connection.RemoteIpAddress);
        if (baggage.Length > 0 && last is { } before && before.Baggage == baggage)
        {
            if (mayAnswer)
                http.Response.Headers[Peers.HostHeader] = context.HostId;
            return (before.Tally, traceParent);
        }
        var origin = Peers.OriginIn(baggage);
        if ((origin is not null || headers.ContainsKey(Peers.HostHeader)) && mayAnswer)
            http.Response.Headers[Peers.HostHeader] = context.HostId;
        if (origin is not null)
        {
            var tally = context.Arrival(Peers.MeepScheme + origin);
            last = new LastOrigin(baggage, tally);
            return (tally, traceParent);
        }
        var from = http.Connection.RemoteIpAddress is { } address ? Peers.ClientOf(address) : "unknown";
        return (context.Arrival(from), traceParent);
    }

    /// <summary>The baggage a Listen last took an origin from, and that origin's counts.</summary>
    internal sealed record LastOrigin(string Baggage, Tally Tally);

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

    /// <summary>
    /// Whether requests must be signed, proving who sent them (Q253): <c>required</c> refuses, with 401, one without
    /// <c>Signature-Input</c>, <c>Signature</c> and <c>Meep-Credential</c>, before it reaches the pipeline. <c>Verify</c>
    /// still checks them. <c>optional</c>, the default, lets every request in.
    /// </summary>
    public RequiredSignature Signature { get; init; }
}

/// <summary>Whether a Listen lets in requests that aren't signed.</summary>
public enum RequiredSignature
{
    /// <summary>Every request comes in; a pipeline's <c>Verify</c> decides what to make of it.</summary>
    Optional,

    /// <summary>A request without a signature and a credential is refused before it reaches the pipeline.</summary>
    Required,
}
