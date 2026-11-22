using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.Shared;

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
        if (parameters.Root is not null)
            return "reads files only when root= is set";
        var url = parameters.Url.Evaluate(message);
        return Addresses.IsWeb(url, out _) ? null : $"needs an http or https address, not {url}";
    }

    // One client for every Fetch, so connections are shared and pooled. Each request sets its own timeout.
    internal static readonly HttpClient Client = CreateClient();

    /// <inheritdoc/>
    protected override async ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        if (!TryAddress(message, context, out var url, out var uri))
            return;

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
            context.Emit(MessageFor(url, response, body));
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

    // The address worked out for a message, if it's one on the web. False, having emitted why, if it isn't.
    private bool TryAddress(Message message, ModuleContext context, out string url, out Uri? uri)
    {
        uri = null;
        try
        {
            url = parameters.Url.Evaluate(message);
        }
        catch (EvaluationException exception)
        {
            url = "";
            context.EmitError(exception.Message, exception);
            return false;
        }
        if (Addresses.IsWeb(url, out uri))
            return true;
        context.EmitError($"Fetch.HTTP needs an http or https address, not {url}");
        return false;
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

    /// <summary>
    /// A response read in full, as a message: an <see cref="HttpResponse"/> for a success, or else an
    /// <see cref="HttpErrorMessage"/> with the body as its text.
    /// </summary>
    internal static Message MessageFor(string url, HttpResponseMessage response, byte[] body)
    {
        var text = Decode(body, response.Content.Headers.ContentType);
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            return new HttpErrorMessage
            {
                Error = $"{url} answered {status} {response.ReasonPhrase}".TrimEnd(),
                Url = url,
                StatusCode = status,
                Text = text,
            };
        }
        return new HttpResponse
        {
            Url = url,
            StatusCode = status,
            ReasonPhrase = response.ReasonPhrase,
            ContentType = response.Content.Headers.ContentType?.MediaType,
            Headers = Headers(response),
            Body = body,
            Text = text,
        };
    }

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

    internal static string Describe(TimeSpan time) => DurationText.Write(time);

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
/// current folder, or from <c>root=</c>. A file that's missing, unreadable or over the size limit becomes an
/// <see cref="ErrorMessage"/>. What a <c>{msg…}</c> hole gives can't make the path absolute, start it with <c>~</c>
/// or climb out with <c>..</c>, unless <c>any-path=true</c>.
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
        IReadOnlyList<TextPiece> pieces;
        try
        {
            pieces = parameters.Url.Pieces(message);
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return;
        }
        var address = string.Concat(pieces.Select(piece => piece.Text));
        if (Addresses.FilePath(address) is null)
        {
            context.EmitError($"Fetch.File needs a file path or a file:// address, not {address}");
            return;
        }

        string path;
        try
        {
            path = Paths.Resolve(pieces, parameters.Root, parameters.AnyPath);
        }
        catch (PathRefusedException exception)
        {
            context.EmitError($"Fetch refused a path: {exception.Message}", exception);
            return;
        }
        try
        {
            // Checked before opening, since opening a named pipe waits for a writer, and again once it's open, in case
            // the path was changed in between.
            if (File.Exists(path) && !FileKinds.IsRegular(path))
            {
                context.EmitError($"{path} isn't a regular file, such as a device or a named pipe, so it isn't read");
                return;
            }
            var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Options = FileOptions.Asynchronous });
            await using (stream.ConfigureAwait(false))
            {
                if (!FileKinds.IsRegular(stream.SafeFileHandle))
                {
                    context.EmitError($"{path} isn't a regular file, such as a device or a named pipe, so it isn't read");
                    return;
                }
                if (stream.Length > parameters.MaxSize)
                {
                    context.EmitError($"{path} is {stream.Length} bytes, over the limit of {parameters.MaxSize}");
                    return;
                }
                // Read to its end, within the limit, rather than trusting the length: a file can grow while it's read.
                var bytes = await Bodies.ReadAsync(stream, declaredLength: -1, parameters.MaxSize, cancellationToken).ConfigureAwait(false);
                context.Emit(new FileText(path, Bodies.Decode(bytes)));
            }
        }
        catch (BodyTooLargeException)
        {
            context.EmitError($"{path} grew past the limit of {parameters.MaxSize} bytes while it was read");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.EmitError($"Couldn't read {path}: {exception.Message}", exception);
        }
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

    /// <summary>
    /// For files: a folder every path has to stay inside. Relative paths start from it, and a path that leads anywhere
    /// else becomes an error. With it set, this Fetch only reads files.
    /// </summary>
    public string? Root { get; init; }

    /// <summary>
    /// For files: lets a message choose any path, through a <c>{msg…}</c> hole: an absolute one, one starting with
    /// <c>~</c>, or one climbing out with <c>..</c>. Off unless set.
    /// </summary>
    public bool AnyPath { get; init; }
}
