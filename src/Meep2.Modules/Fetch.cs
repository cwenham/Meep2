using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Fetch's HTTP inflection: fetches a web address for each message it receives, and emits the response as
/// <see cref="HttpResponse"/>. In Meeplang: <c>Fetch "https://api.example.com/stops/{msg.StopId}"</c>.
/// </summary>
/// <remarks>
/// A status that isn't a success becomes an <see cref="HttpErrorMessage"/>. A network failure, a timeout, or a body
/// over the size limit becomes an <see cref="ErrorMessage"/>. The body is read into memory.
/// </remarks>
[Verb("Fetch", Inflection = "HTTP")]
[Emits(typeof(HttpResponse))]
public sealed class HttpFetch(FetchParameters parameters) : AInflection<Message>, IChosenByParameters<FetchParameters>, ISendsTo
{
    /// <inheritdoc/>
    public Computed<string> Address => parameters.Url;

    /// <summary>Takes http and https addresses.</summary>
    public static string? WhyNot(FetchParameters parameters, Message message)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var url = parameters.Url.Evaluate(message);
        return Addresses.IsWeb(url, out _) ? null : $"needs an http or https address, not {url}";
    }

    // One client for every Fetch, so connections are shared and pooled. Each request sets its own timeout.
    internal static readonly HttpClient Client = CreateClient();

    /// <inheritdoc/>
    protected override async ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        string url;
        try
        {
            url = parameters.Url.Evaluate(message);
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return;
        }

        if (!Addresses.IsWeb(url, out var uri))
        {
            context.EmitError($"Fetch.HTTP needs an http or https address, not {url}");
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(parameters.Timeout);
        HttpResponseMessage? response = null;
        HttpResponseMessage? answer = null; // kept when the lease takes the response, for counting a failure after
        var endpoint = Peers.EndpointOf(uri!);
        var tally = endpoint is null ? Tally.None : context.Destination(endpoint);
        try
        {
            var reached = await SendAsync(HttpMethod.Get, uri!, null, HttpCompletionOption.ResponseHeadersRead, context, timeout.Token).ConfigureAwait(false);
            response = answer = reached.Response;
            tally = reached.Counted(endpoint, tally, context);
            tally.Count(sent: 1, received: 1);
            tally.Status((int)response.StatusCode);
            if (parameters.Stream && response.IsSuccessStatusCode)
            {
                // From here the lease owns the response, and disposes it when it closes.
                var streamed = response;
                response = null;
                await EmitStreamedAsync(streamed, url, context, tally, timeout.Token).ConfigureAwait(false);
                return;
            }
            var body = await ReadBodyAsync(response.Content, url, parameters.MaxSize, timeout.Token).ConfigureAwait(false);
            context.CountBytes(body.Length, 0);
            tally.Count(bytesReceived: body.Length);
            var text = Decode(body, response.Content.Headers.ContentType);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                context.Emit(new HttpErrorMessage
                {
                    Error = $"{url} answered {status} {response.ReasonPhrase}".TrimEnd(),
                    Url = url,
                    StatusCode = status,
                    Text = text,
                });
                return;
            }

            context.Emit(new HttpResponse
            {
                Url = url,
                StatusCode = status,
                ReasonPhrase = response.ReasonPhrase,
                ContentType = response.Content.Headers.ContentType?.MediaType,
                Headers = Headers(response),
                Body = body,
                Text = text,
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Failed(answer, tally);
            context.EmitError($"{url} didn't answer within {Describe(parameters.Timeout)}");
        }
        catch (HttpRequestException exception)
        {
            Failed(answer, tally);
            context.EmitError($"Couldn't fetch {url}: {exception.Message}", exception);
        }
        catch (TooLargeException exception)
        {
            Failed(answer, tally);
            context.EmitError(exception.Message, exception);
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>The most redirects followed for one request, as <see cref="HttpClientHandler"/> follows by default.</summary>
    private const int MaxRedirects = 50;

    /// <summary>
    /// Sends a request, saying where it comes from (<see cref="MeepHosts.Mark"/>), and follows redirects itself, as
    /// <see cref="HttpClient"/> would (Q217): to http or https, never from https to http, a POST becoming a GET after
    /// 301, 302 or 300, and anything but a HEAD after 303, and the body sent again after 307 or 308, unless it was
    /// streamed. A redirect to another host or port asks it whether it's Meep, and never gives it the origin.
    /// </summary>
    /// <param name="method">GET or POST.</param>
    /// <param name="uri">Where to.</param>
    /// <param name="content">The body, or null; the caller disposes it.</param>
    /// <param name="completion">Whether to wait for the whole response, or only its headers.</param>
    /// <param name="context">The module sending it, for where it comes from.</param>
    /// <param name="cancellationToken">Gives up.</param>
    /// <param name="sending">Told the bytes of a body each time one is sent, for counting.</param>
    internal static async Task<Reached> SendAsync(HttpMethod method, Uri uri, HttpContent? content, HttpCompletionOption completion, ModuleContext context, CancellationToken cancellationToken, Action<long>? sending = null)
    {
        var first = Peers.EndpointOf(uri);
        var endpoint = first;
        for (var hop = 0; ; hop++)
        {
            // Not disposed: that would dispose the body, which a 307 or 308 sends again. The caller disposes it.
            var request = new HttpRequestMessage(method, uri) { Content = content };
            var marked = MeepHosts.Mark(request.Headers, endpoint, uri, context, ask: endpoint != first);
            var response = await Client.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
            if (content is not null && content is not StreamContent)
                sending?.Invoke(content.Headers.ContentLength ?? 0);
            var id = MeepHosts.Answered(endpoint, response.Headers, marked);
            var status = (int)response.StatusCode;
            if (hop >= MaxRedirects || RedirectOf(response, uri) is not { } next || (status is 307 or 308 && content is StreamContent))
                return new Reached(response, endpoint, id);
            if ((status is 300 or 301 or 302 && method == HttpMethod.Post) || (status == 303 && method != HttpMethod.Get && method != HttpMethod.Head))
            {
                method = HttpMethod.Get;
                content = null;
            }
            response.Dispose();
            uri = next;
            endpoint = Peers.EndpointOf(next);
        }
    }

    // Where a redirect leads, or null for a response that isn't one this follows.
    private static Uri? RedirectOf(HttpResponseMessage response, Uri from)
    {
        if ((int)response.StatusCode is not (300 or 301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
            return null;
        if (!location.IsAbsoluteUri)
            location = new Uri(from, location);
        if (location.Scheme is not ("http" or "https") || (from.Scheme == "https" && location.Scheme == "http"))
            return null;
        return location;
    }

    /// <summary>The response a request came to, after any redirects: where, and the Meep host there, if it is one.</summary>
    internal readonly record struct Reached(HttpResponseMessage Response, string? Endpoint, string? MeepId)
    {
        /// <summary>
        /// The counts for where it came to, knowing whether it's Meep there: <paramref name="tally"/>, for
        /// <paramref name="asked"/>, unless a redirect led somewhere else (Q217).
        /// </summary>
        public Tally Counted(string? asked, Tally tally, ModuleContext context)
        {
            if (Endpoint != asked)
                tally = Endpoint is null ? Tally.None : context.Destination(Endpoint);
            tally.Meep(MeepId);
            return tally;
        }
    }

    // A request that failed: one sent, and one failed, unless an answer had been counted already, as failed or rejected.
    internal static void Failed(HttpResponseMessage? answered, Tally tally) =>
        tally.Count(sent: answered is null ? 1 : 0, failed: answered is null || (int)answered.StatusCode < 400 ? 1 : 0);

    // Emits the response with its body unread. A lease keeps the response open until the body has been read and
    // every message made from this one has left the pipeline.
    private static async Task EmitStreamedAsync(HttpResponseMessage response, string url, ModuleContext context, Tally tally, CancellationToken cancellationToken)
    {
        var lease = new OpenResponse(response);
        Stream body;
        try
        {
            body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await lease.CloseAsync().ConfigureAwait(false);
            throw;
        }
        context.Emit(
            new HttpResponse
            {
                Url = url,
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
                ContentType = response.Content.Headers.ContentType?.MediaType,
                Headers = Headers(response),
                Body = default,
                Text = "",
                Streamed = new StreamedBody(new CountingStream(body, (received, sent) =>
                {
                    context.CountBytes(received, sent);
                    tally.Count(bytesReceived: received, bytesSent: sent);
                }), "response body"),
            },
            lease);
    }

    internal static async Task<byte[]> ReadBodyAsync(HttpContent content, string url, long limit, CancellationToken cancellationToken)
    {
        var length = content.Headers.ContentLength;
        if (length > limit)
            throw new TooLargeException($"{url} sent {length} bytes, over the limit of {limit}");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        // The chunk is rented, and only copied from, so nothing keeps it once it's returned; the body is a new array.
        var chunk = ArrayPool<byte>.Shared.Rent(length is { } known ? (int)Math.Clamp(known, 1, ChunkSize) : ChunkSize);
        try
        {
            using var buffer = new MemoryStream(length is { } expected ? (int)Math.Min(expected, int.MaxValue) : 0);
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                    throw new TooLargeException($"{url} sent more than the limit of {limit} bytes");
                buffer.Write(chunk, 0, read);
            }
            return buffer.Length == buffer.Capacity ? buffer.GetBuffer() : buffer.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    // The most read at once from a response's body.
    private const int ChunkSize = 81920;

    internal static string Decode(byte[] body, MediaTypeHeaderValue? contentType)
    {
        var encoding = Encoding.UTF8;
        if (contentType?.CharSet is { Length: > 0 } charset)
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // An unknown character set: fall back to UTF-8.
            }
        }
        return encoding.GetString(body);
    }

    internal static Dictionary<string, string> Headers(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            headers[name] = string.Join(", ", values);
        return headers;
    }

    internal static string Describe(TimeSpan time) =>
        time.TotalSeconds >= 1 ? $"{time.TotalSeconds:0.###}s" : $"{time.TotalMilliseconds:0.###}ms";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            // Followed in SendAsync instead, so origins don't go on to wherever a redirect leads (Q217).
            AllowAutoRedirect = false,
            // Fetch and Send add traceparent themselves, naming their own span (MeepHosts.Mark). .NET's own would name
            // a span of its own that no collector receives, so the other host's spans would hang from nothing.
            ActivityHeadersPropagator = System.Diagnostics.DistributedContextPropagator.CreateNoOutputPropagator(),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Meep2", "1.0"));
        return client;
    }

    internal sealed class TooLargeException(string message) : Exception(message);
}

/// <summary>
/// Fetch's File inflection: reads a file for each message it receives, and emits it as <see cref="FileText"/>.
/// In Meeplang: <c>Fetch "~/config/stops.json"</c> or <c>Fetch "file:///var/data/stops.json"</c>.
/// </summary>
/// <remarks>
/// A path without a scheme is read as a file: <c>~</c> is the home folder, and other relative paths start from the
/// current folder. A file that's missing, unreadable or over the size limit becomes an <see cref="ErrorMessage"/>.
/// </remarks>
[Verb("Fetch", Inflection = "File")]
[Emits(typeof(FileText))]
public sealed class FileFetch(FetchParameters parameters) : AInflection<Message>, IChosenByParameters<FetchParameters>
{
    /// <summary>Takes file paths and file:// addresses, and doesn't stream.</summary>
    public static string? WhyNot(FetchParameters parameters, Message message)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Stream)
            return "doesn't stream, so leave out stream=true";
        var address = parameters.Url.Evaluate(message);
        return Addresses.FilePath(address) is null ? $"needs a file path or a file:// address, not {address}" : null;
    }

    /// <inheritdoc/>
    protected override async ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        string address;
        try
        {
            address = parameters.Url.Evaluate(message);
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return;
        }
        if (Addresses.FilePath(address) is not { } file)
        {
            context.EmitError($"Fetch.File needs a file path or a file:// address, not {address}");
            return;
        }

        var path = Paths.Expand(file);
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > parameters.MaxSize)
            {
                context.EmitError($"{path} is {info.Length} bytes, over the limit of {parameters.MaxSize}");
                return;
            }
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            context.Emit(new FileText(path, text));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.EmitError($"Couldn't read {path}: {exception.Message}", exception);
        }
    }
}

/// <summary>Tells web addresses and file paths apart, for Fetch's inflections.</summary>
internal static class Addresses
{
    /// <summary>Whether an address is an http or https address.</summary>
    public static bool IsWeb(string address, out Uri? uri) =>
        Uri.TryCreate(address, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// The file path an address names: a file:// address, or a path such as <c>~/data.json</c>, <c>/var/data.json</c>
    /// or <c>data.json</c>. Null for an address with another scheme, such as https.
    /// </summary>
    public static string? FilePath(string address)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
            return uri.IsFile ? uri.LocalPath : null;
        return address.Length > 0 ? address : null;
    }
}

/// <summary>Parameters for Fetch's inflections, <see cref="HttpFetch"/> and <see cref="FileFetch"/>.</summary>
public sealed record FetchParameters
{
    /// <summary>The address to fetch, worked out for each message.</summary>
    [Positional]
    public required Computed<string> Url { get; init; }

    /// <summary>How long to wait for the whole response. 100 seconds unless set.</summary>
    public TimeSpan Timeout
    {
        get;
        init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(Timeout), "The timeout must be longer than zero.");
    } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Leaves a successful response's body unread, for one module to read, such as Send passing it on to a client
    /// without holding it all in memory (Q51). The connection stays open until then. Only HTTP streams.
    /// </summary>
    public bool Stream { get; init; }

    /// <summary>The largest body to read, in bytes. 16 MiB unless set.</summary>
    public long MaxSize
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxSize), "The size limit must be at least 1 byte.");
    } = 16 * 1024 * 1024;
}
