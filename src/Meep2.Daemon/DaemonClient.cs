using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Meep2.Daemon;

/// <summary>Talks to a <see cref="MeepDaemon"/> through its control socket, as the meep command does.</summary>
public sealed class DaemonClient : IDisposable
{
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _http;
    private readonly string? _token;

    /// <summary>Connects to the daemon on a socket, showing a token if one is given.</summary>
    public DaemonClient(string socketPath, string? token = null)
    {
        ArgumentNullException.ThrowIfNull(socketPath);
        SocketPath = socketPath;
        _token = token;
        var handler = _handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://meep/"), Timeout = Timeout.InfiniteTimeSpan };
        if (token is not null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>The control socket.</summary>
    public string SocketPath { get; }

    /// <summary>The daemon, and what this client may do.</summary>
    public Task<DaemonInfo> InfoAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, "", null, DaemonJson.Default.DaemonInfo, cancellationToken);

    /// <summary>Asks the daemon to load a pipeline file and start it. Returns once it's running.</summary>
    public Task<PipelineSummary> StartAsync(string path, bool developerMode = false, CancellationToken cancellationToken = default) =>
        StartAsync(path, developerMode, watch: false, cancellationToken);

    /// <summary>Asks the daemon to start a pipeline file, and if <paramref name="watch"/> is set, to reload it whenever the file changes.</summary>
    public Task<PipelineSummary> StartAsync(string path, bool developerMode, bool watch, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "pipelines", JsonContent.Create(new StartRequest(AddressFor(path), developerMode, watch), DaemonJson.Default.StartRequest), DaemonJson.Default.PipelineSummary, cancellationToken);

    /// <summary>Sends a pipeline's text for the daemon to start, as <c>meep start -</c> does with standard input (goal 7).</summary>
    public Task<PipelineSummary> StartTextAsync(string name, string text, bool developerMode = false, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "pipelines", JsonContent.Create(new StartRequest("", developerMode) { Text = text, Name = name }, DaemonJson.Default.StartRequest), DaemonJson.Default.PipelineSummary, cancellationToken);

    // A file's full path, since the daemon's working folder isn't this one; other addresses as they are.
    private static string AddressFor(string path) =>
        PipelineAddress.Parse(path) is { IsFile: true } file ? file.Location : path;

    /// <summary>The text a running pipeline runs from.</summary>
    public Task<SourceText> SourceAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, $"pipelines/{Uri.EscapeDataString(name)}/source", null, DaemonJson.Default.SourceText, cancellationToken);

    /// <summary>
    /// Reads a running pipeline's file again and changes the pipeline to match, plugins included. It's refused if that
    /// would undo edits that aren't in the file yet, unless <paramref name="force"/> is set (Q120).
    /// </summary>
    public Task<ChangeResult> ReloadAsync(string name, bool force = false, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"pipelines/{Uri.EscapeDataString(name)}/reload{(force ? "?force=true" : "")}", null, DaemonJson.Default.ChangeResult, cancellationToken);

    /// <summary>Edits a running pipeline's text, changes the pipeline to match, and commits the text (Q32).</summary>
    public Task<ChangeResult> EditAsync(string name, EditRequest edits, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"pipelines/{Uri.EscapeDataString(name)}/edits", JsonContent.Create(edits, DaemonJson.Default.EditRequest), DaemonJson.Default.ChangeResult, cancellationToken);

    /// <summary>The pipelines running.</summary>
    public Task<IReadOnlyList<PipelineSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, "pipelines", null, DaemonJson.Default.IReadOnlyListPipelineSummary, cancellationToken);

    /// <summary>A pipeline, with its modules' counts and its links' counts (<see cref="PipelineDetail.Links"/>).</summary>
    public Task<PipelineDetail> GetAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, $"pipelines/{Uri.EscapeDataString(name)}", null, DaemonJson.Default.PipelineDetail, cancellationToken);

    /// <summary>A pipeline as a graph: its modules, where each is written, and the links between them.</summary>
    public Task<PipelineGraph> GraphAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, $"pipelines/{Uri.EscapeDataString(name)}/graph", null, DaemonJson.Default.PipelineGraph, cancellationToken);

    /// <summary>
    /// Each change to a pipeline's graph as it happens, until the pipeline finishes or the token is
    /// cancelled. It listens to the stats stream, so it costs a stats event an hour besides.
    /// </summary>
    public async IAsyncEnumerable<GraphNotice> GraphChangesAsync(string name, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var (kind, data) in EventsAsync($"pipelines/{Uri.EscapeDataString(name)}/stats?every=1h", cancellationToken).ConfigureAwait(false))
        {
            if (kind == "graph")
                yield return JsonSerializer.Deserialize(data, DaemonJson.Default.GraphNotice)!;
        }
    }

    /// <summary>Stops a pipeline, letting its messages finish. Returns once it has.</summary>
    public Task StopAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"pipelines/{Uri.EscapeDataString(name)}", null, cancellationToken);

    /// <summary>Asks the daemon to read its tokens file now, ending what tokens taken out of it opened.</summary>
    public Task ReloadTokensAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "tokens/reload", null, cancellationToken);

    /// <summary>Asks the daemon to stop its pipelines and then itself.</summary>
    public Task StopDaemonAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "stop", null, cancellationToken);

    /// <summary>Sends text into a module of a running pipeline, or JSON if <paramref name="json"/> is set.</summary>
    public Task<Injected> InjectAsync(string pipeline, string module, string content, bool json = false, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"pipelines/{Uri.EscapeDataString(pipeline)}/modules/{Uri.EscapeDataString(module)}/inject",
            new StringContent(content, Encoding.UTF8, json ? "application/json" : "text/plain"), DaemonJson.Default.Injected, cancellationToken);

    /// <summary>
    /// A pipeline's modules and counts, and its links' counts, every so often, until it finishes or the token is
    /// cancelled. The counts only go up, except when the pipeline restarts, so the difference between two is what
    /// happened between them.
    /// </summary>
    public async IAsyncEnumerable<PipelineDetail> WatchAsync(string name, TimeSpan every, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var milliseconds = (long)every.TotalMilliseconds;
        await foreach (var (kind, data) in EventsAsync($"pipelines/{Uri.EscapeDataString(name)}/stats?every={milliseconds}ms", cancellationToken).ConfigureAwait(false))
        {
            if (kind == "stats")
                yield return JsonSerializer.Deserialize(data, DaemonJson.Default.PipelineDetail)!;
        }
    }

    /// <summary>
    /// The messages passing a module, as the daemon writes them in JSON, until the pipeline finishes or the token is
    /// cancelled. Each comes with how many the tap has dropped so far because the reader fell behind.
    /// </summary>
    public async IAsyncEnumerable<(JsonElement Message, long Dropped)> TapAsync(
        string pipeline, string module, string? where = null, double sample = 1, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder($"pipelines/{Uri.EscapeDataString(pipeline)}/modules/{Uri.EscapeDataString(module)}/tap?");
        if (where is not null)
            query.Append("where=").Append(Uri.EscapeDataString(where)).Append('&');
        if (sample < 1)
            query.Append("sample=").Append(sample.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await foreach (var (kind, data) in EventsAsync(query.ToString(), cancellationToken).ConfigureAwait(false))
        {
            if (kind != "message")
                continue;
            using var document = JsonDocument.Parse(data);
            yield return (document.RootElement.GetProperty("message").Clone(), document.RootElement.GetProperty("dropped").GetInt64());
        }
    }

    /// <summary>
    /// Opens a WebSocket that carries the whole API, both ways (Q108). Requests and replies are JSON frames, as
    /// <see cref="DaemonSocket"/> describes.
    /// </summary>
    public async Task<DaemonSocket> OpenSocketAsync(CancellationToken cancellationToken = default)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        if (_token is not null)
            socket.Options.SetRequestHeader("Authorization", $"Bearer {_token}");
        try
        {
            using var invoker = new HttpMessageInvoker(_handler, disposeHandler: false);
            await socket.ConnectAsync(new Uri("ws://meep/ws"), invoker, cancellationToken).ConfigureAwait(false);
            return new DaemonSocket(socket);
        }
        catch (WebSocketException exception)
        {
            var status = (int)socket.HttpStatusCode;
            socket.Dispose();
            throw status == 0
                ? NoDaemon(exception)
                : new DaemonException(status, $"The daemon refused the WebSocket with {status}. {(status is 401 or 403 ? "The token, or your user, may not use it." : "")}".TrimEnd(), innerException: exception);
        }
    }

    /// <summary>The message for a daemon that isn't there, saying how to start one.</summary>
    public DaemonException NoDaemon(Exception? cause = null) => new(0, $"""
        No meep daemon is running on {SocketPath}.
        Start one in a terminal of its own with:  meep daemon
        Or set it up to start when you log in:    meep daemon install
        """, innerException: cause);

    /// <inheritdoc/>
    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }

    // Server-sent events: each is an event line and a data line of JSON, ending with a blank line.
    private async IAsyncEnumerable<(string Kind, string Data)> EventsAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await SendRawAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var kind = "message";
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
                kind = line["event: ".Length..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
                yield return (kind, line["data: ".Length..]);
            else if (line.Length == 0)
                kind = "message";
            if (kind == "end")
                yield break;
        }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await SendRawAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync(type, cancellationToken).ConfigureAwait(false)
            ?? throw new DaemonException((int)response.StatusCode, "The daemon's answer was empty.");
    }

    private async Task SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await SendRawAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
    }

    // Sends a request, turning a refusal into a DaemonException that says why.
    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        // A large body waits for the daemon to say it will take it, so a refusal (413) arrives as an answer rather than
        // as a connection closed in the middle of sending.
        if (request.Content?.Headers.ContentLength > 64 * 1024)
            request.Headers.ExpectContinue = true;
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.InnerException is SocketException or IOException)
        {
            throw NoDaemon(exception);
        }
        if (response.IsSuccessStatusCode)
            return response;
        using (response)
        {
            Problem? problem = null;
            try
            {
                problem = await response.Content.ReadFromJsonAsync(DaemonJson.Default.Problem, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Not one of the daemon's own answers.
            }
            throw new DaemonException((int)response.StatusCode, problem?.Error ?? $"The daemon answered {(int)response.StatusCode} {response.ReasonPhrase}.", problem?.Details);
        }
    }
}
