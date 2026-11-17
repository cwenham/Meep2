using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using Meep2.Cli;
using Meep2.Plugins;

namespace Meep2.Tests.Support;

/// <summary>
/// <c>meep lsp</c> running in the test, talked to as an editor talks to it: requests answered by id, and the
/// notifications it sends kept in the order they came.
/// </summary>
public sealed class LspSession : IAsyncDisposable
{
    private readonly Pipe _toServer = new();
    private readonly Pipe _fromServer = new();
    private readonly Task<int> _server;
    private readonly Task _reading;
    private readonly Stream _writer;
    private readonly Dictionary<int, TaskCompletionSource<JsonNode>> _waiting = [];
    private readonly List<JsonNode> _notifications = [];
    private readonly Lock _lock = new();
    private int _next;

    public LspSession(PluginOptions? plugins = null)
    {
        var server = new LanguageServer(_toServer.Reader.AsStream(), _fromServer.Writer.AsStream(), plugins);
        _server = Task.Run(() => server.RunAsync(CancellationToken.None));
        _writer = _toServer.Writer.AsStream();
        _reading = Task.Run(ReadAsync);
    }

    /// <summary>Sends a request and returns its result, or throws with the error's message if it has one.</summary>
    public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters = null)
    {
        var answer = await SendRequestAsync(method, parameters);
        if (answer["error"] is { } error)
            throw new InvalidOperationException(error["message"]!.GetValue<string>());
        return answer["result"];
    }

    /// <summary>Sends a request and returns the whole answer, error or result.</summary>
    public async Task<JsonNode> SendRequestAsync(string method, JsonObject? parameters = null)
    {
        var done = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        int id;
        lock (_lock)
        {
            id = ++_next;
            _waiting[id] = done;
        }
        await WriteAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters });
        return await Wait.For(done.Task);
    }

    /// <summary>Sends a notification, which has no answer.</summary>
    public Task NotifyAsync(string method, JsonObject parameters) => WriteAsync(new JsonObject { ["method"] = method, ["params"] = parameters });

    public Task OpenAsync(string uri, string text, int version = 1) => NotifyAsync("textDocument/didOpen", new JsonObject
    {
        ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = "meeplang", ["version"] = version, ["text"] = text },
    });

    public Task ChangeAsync(string uri, string text, int version) => NotifyAsync("textDocument/didChange", new JsonObject
    {
        ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
        ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
    });

    /// <summary>
    /// Waits until the server has handled everything sent so far: it answers in order, so once a request sent now is
    /// answered, the notifications it sent before are here.
    /// </summary>
    public Task SyncAsync() => SendRequestAsync("meep/verbs");

    /// <summary>The notifications of a method it has sent so far, oldest first.</summary>
    public IReadOnlyList<JsonNode> Notifications(string method)
    {
        lock (_lock)
            return [.. _notifications.Where(n => n["method"]?.GetValue<string>() == method).Select(n => n["params"]!)];
    }

    public static JsonObject Document(string uri) => new() { ["uri"] = uri };

    private async Task WriteAsync(JsonObject message)
    {
        message["jsonrpc"] = "2.0";
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _writer.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        await _writer.WriteAsync(body);
        await _writer.FlushAsync();
    }

    private async Task ReadAsync()
    {
        var reader = _fromServer.Reader.AsStream();
        var header = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            header.Clear();
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await reader.ReadAsync(one) == 0)
                    return;
                header.Append((char)one[0]);
            }
            var lengthLine = header.ToString().Split("\r\n").First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var body = new byte[int.Parse(lengthLine["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture)];
            await reader.ReadExactlyAsync(body);
            var message = JsonNode.Parse(body)!;
            lock (_lock)
            {
                if (message["id"] is { } id && message["method"] is null && _waiting.Remove(id.GetValue<int>(), out var waiting))
                    waiting.SetResult(message);
                else
                    _notifications.Add(message);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await WriteAsync(new JsonObject { ["id"] = 0, ["method"] = "shutdown" });
        await WriteAsync(new JsonObject { ["method"] = "exit" });
        await _toServer.Writer.CompleteAsync();
        await Wait.For(_server);
        await _fromServer.Writer.CompleteAsync();
        await Wait.For(_reading);
    }
}
