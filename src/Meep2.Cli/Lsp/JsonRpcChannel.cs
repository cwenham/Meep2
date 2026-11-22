using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meep2.Cli;

/// <summary>
/// JSON-RPC over a pair of streams, framed as LSP frames it: a <c>Content-Length</c> header, a blank line, then the
/// JSON. Reads messages, and writes answers, errors and notifications.
/// </summary>
/// <param name="input">What the client sends.</param>
/// <param name="output">Where replies go.</param>
internal sealed class JsonRpcChannel(Stream input, Stream output)
{
    /// <summary>The largest message it reads: 32 MiB, far more than any file's text.</summary>
    internal const int MaxMessage = 32 * 1024 * 1024;

    /// <summary>Answers a request.</summary>
    public Task RespondAsync(JsonNode? id, JsonNode? result, CancellationToken cancellationToken) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result }, cancellationToken);

    /// <summary>An error answer; with a null id, for a message whose id couldn't be read, as JSON-RPC says.</summary>
    public Task RespondErrorAsync(JsonNode? id, int code, string message, CancellationToken cancellationToken, JsonNode? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null)
            error["data"] = data.DeepClone();
        return WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = error }, cancellationToken);
    }

    /// <summary>Sends a notification, which has no answer.</summary>
    public Task NotifyAsync(string method, JsonNode parameters, CancellationToken cancellationToken) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, cancellationToken);

    /// <summary>
    /// The next message, or null once the stream ends or its framing can't be followed. A message too large, or not
    /// JSON, is answered with an error and skipped (S10).
    /// </summary>
    public async Task<JsonNode?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var length = await ReadLengthAsync(cancellationToken).ConfigureAwait(false);
            if (length is null)
                return null;
            if (length < 0)
            {
                // Without a length, where the next message starts can't be known, so the server stops, saying why.
                await RespondErrorAsync(null, -32700, "meep lsp read a message without a Content-Length it could follow, so it has stopped.", cancellationToken).ConfigureAwait(false);
                return null;
            }
            if (length > MaxMessage)
            {
                await SkipAsync(length.Value, cancellationToken).ConfigureAwait(false);
                await RespondErrorAsync(null, -32600, $"meep lsp skipped a message of {length} bytes: messages can be at most {MaxMessage}.", cancellationToken).ConfigureAwait(false);
                continue;
            }
            var body = new byte[length.Value];
            await input.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
            try
            {
                return JsonNode.Parse(body) ?? new JsonObject();
            }
            catch (JsonException exception)
            {
                await RespondErrorAsync(null, -32700, $"meep lsp couldn't read a message as JSON: {exception.Message}", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // A message, framed as LSP frames them.
    private async Task WriteAsync(JsonNode message, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // The length a message's headers give: null once the stream ends, and -1 if there's none it can follow.
    private async Task<long?> ReadLengthAsync(CancellationToken cancellationToken)
    {
        long length = -1;
        var malformed = false;
        while (true)
        {
            var line = await ReadHeaderLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                return null;
            if (line.Length == 0)
                return malformed ? -1 : length;
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                malformed = !long.TryParse(line["Content-Length:".Length..].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out length);
        }
    }

    // Reads past a body without keeping it.
    private async Task SkipAsync(long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        while (length > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return;
            length -= read;
        }
    }

    private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await input.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
                return line.Length == 0 ? null : line.ToString();
            if (one[0] == '\n')
                return line.ToString().TrimEnd('\r');
            line.Append((char)one[0]);
        }
    }
}
