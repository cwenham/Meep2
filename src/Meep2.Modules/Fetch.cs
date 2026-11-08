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
public sealed class HttpFetch(FetchParameters parameters) : AInflection<Message>, IChosenByParameters<FetchParameters>
{
    /// <summary>Takes http and https addresses.</summary>
    public static string? WhyNot(FetchParameters parameters, Message message)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var url = parameters.Url.Evaluate(message);
        return Addresses.IsWeb(url, out _) ? null : $"needs an http or https address, not {url}";
    }

    // One client for every Fetch, so connections are shared and pooled. Each request sets its own timeout.
    private static readonly HttpClient s_client = CreateClient();

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
        try
        {
            using var response = await s_client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var body = await ReadBodyAsync(response.Content, url, timeout.Token).ConfigureAwait(false);
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
            context.EmitError($"{url} didn't answer within {Describe(parameters.Timeout)}");
        }
        catch (HttpRequestException exception)
        {
            context.EmitError($"Couldn't fetch {url}: {exception.Message}", exception);
        }
        catch (TooLargeException exception)
        {
            context.EmitError(exception.Message, exception);
        }
    }

    private async Task<byte[]> ReadBodyAsync(HttpContent content, string url, CancellationToken cancellationToken)
    {
        var limit = parameters.MaxSize;
        if (content.Headers.ContentLength > limit)
            throw new TooLargeException($"{url} sent {content.Headers.ContentLength} bytes, over the limit of {limit}");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
                throw new TooLargeException($"{url} sent more than the limit of {limit} bytes");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string Decode(byte[] body, MediaTypeHeaderValue? contentType)
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

    private static Dictionary<string, string> Headers(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            headers[name] = string.Join(", ", values);
        return headers;
    }

    private static string Describe(TimeSpan time) =>
        time.TotalSeconds >= 1 ? $"{time.TotalSeconds:0.###}s" : $"{time.TotalMilliseconds:0.###}ms";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Meep2", "1.0"));
        return client;
    }

    private sealed class TooLargeException(string message) : Exception(message);
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
    /// <summary>Takes file paths and file:// addresses.</summary>
    public static string? WhyNot(FetchParameters parameters, Message message)
    {
        ArgumentNullException.ThrowIfNull(parameters);
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

    /// <summary>The largest body to read, in bytes. 16 MiB unless set.</summary>
    public long MaxSize
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxSize), "The size limit must be at least 1 byte.");
    } = 16 * 1024 * 1024;
}
