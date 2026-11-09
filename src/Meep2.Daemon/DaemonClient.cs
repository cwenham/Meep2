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
        SendAsync(HttpMethod.Post, "pipelines", JsonContent.Create(new StartRequest(Path.GetFullPath(path), developerMode), DaemonJson.Default.StartRequest), DaemonJson.Default.PipelineSummary, cancellationToken);

    /// <summary>The pipelines running.</summary>
    public Task<IReadOnlyList<PipelineSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, "pipelines", null, DaemonJson.Default.IReadOnlyListPipelineSummary, cancellationToken);

    /// <summary>A pipeline, with its modules' counts.</summary>
    public Task<PipelineDetail> GetAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, $"pipelines/{Uri.EscapeDataString(name)}", null, DaemonJson.Default.PipelineDetail, cancellationToken);

    /// <summary>Stops a pipeline, letting its messages finish. Returns once it has.</summary>
    public Task StopAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"pipelines/{Uri.EscapeDataString(name)}", null, cancellationToken);

    /// <summary>Asks the daemon to stop its pipelines and then itself.</summary>
    public Task StopDaemonAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "stop", null, cancellationToken);

    /// <summary>Sends text into a module of a running pipeline, or JSON if <paramref name="json"/> is set.</summary>
    public Task<Injected> InjectAsync(string pipeline, string module, string content, bool json = false, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"pipelines/{Uri.EscapeDataString(pipeline)}/modules/{Uri.EscapeDataString(module)}/inject",
            new StringContent(content, Encoding.UTF8, json ? "application/json" : "text/plain"), DaemonJson.Default.Injected, cancellationToken);

    /// <summary>A pipeline's modules and counts, every so often, until it finishes or the token is cancelled.</summary>
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

/// <summary>
/// A WebSocket to the daemon, carrying its API both ways (Q108). Send requests with <see cref="SendAsync"/> and read
/// what comes back with <see cref="ReceiveAsync"/>: answers, and events from streams, each with the id of the request
/// it belongs to.
/// </summary>
/// <remarks>
/// A request is <c>{"id": 1, "method": "GET", "path": "/pipelines"}</c>, with a body in <c>json</c> or <c>text</c>.
/// An answer is <c>{"id": 1, "status": 200, "body": …}</c>, and a stream's events are
/// <c>{"id": 1, "event": "stats", "data": …}</c> until one called <c>end</c>. <c>{"id": 1, "cancel": true}</c>
/// stops a stream.
/// </remarks>
public sealed class DaemonSocket : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sending = new(1, 1);

    internal DaemonSocket(ClientWebSocket socket) => _socket = socket;

    /// <summary>Sends a request. Its answer, or its events, come back with the same id.</summary>
    /// <param name="id">The request's id, which the replies carry.</param>
    /// <param name="method">GET, POST or DELETE.</param>
    /// <param name="path">The path, as the HTTP API has it, such as <c>/pipelines/ticker/stats?every=1s</c>.</param>
    /// <param name="json">A JSON body, if any.</param>
    /// <param name="text">A text body, if any.</param>
    public Task SendAsync(string id, string method, string path, JsonElement? json = null, string? text = null, CancellationToken cancellationToken = default) =>
        SendFrameAsync(writer =>
        {
            writer.WriteString("id", id);
            writer.WriteString("method", method);
            writer.WriteString("path", path);
            if (json is { } body)
            {
                writer.WritePropertyName("json");
                body.WriteTo(writer);
            }
            if (text is not null)
                writer.WriteString("text", text);
        }, cancellationToken);

    /// <summary>Stops a stream the daemon is sending.</summary>
    public Task CancelAsync(string id, CancellationToken cancellationToken = default) =>
        SendFrameAsync(writer =>
        {
            writer.WriteString("id", id);
            writer.WriteBoolean("cancel", true);
        }, cancellationToken);

    /// <summary>The next frame from the daemon, or null once the connection has closed.</summary>
    public async Task<JsonElement?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        ValueWebSocketReceiveResult result;
        do
        {
            try
            {
                result = await _socket.ReceiveAsync(buffer.GetMemory(4096), cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            buffer.Advance(result.Count);
        }
        while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>Sends a frame as it is, for a client that builds its own.</summary>
    public async Task SendRawAsync(string frame, CancellationToken cancellationToken = default)
    {
        await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Goodbye.", timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
            {
                // It went first.
            }
        }
        _socket.Dispose();
        _sending.Dispose();
    }

    private async Task SendFrameAsync(Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sending.Release();
        }
    }
}
