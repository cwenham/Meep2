using System.Net.WebSockets;
using System.Text;
using Meep2.SDK.Messages;
using Microsoft.Net.Http.Headers;

namespace Meep2.Modules;

/// <summary>Turns messages into WebSocket messages, text or binary, by what they hold (Q160).</summary>
internal static class Frames
{
    private const long Limit = 16 * 1024 * 1024;

    /// <summary>
    /// Text that was given goes as text. Bytes, from a binary WebSocket message or a body whose media type isn't text,
    /// go as binary. Everything else goes as its text.
    /// </summary>
    public static async ValueTask<Frame> ForAsync(Message message, string? text, CancellationToken cancellationToken)
    {
        if (text is not null)
            return Frame.Text(text);
        switch (message)
        {
            case WebSocketBinary binary:
                return new Frame(binary.Body, WebSocketMessageType.Binary);
            case IHasBody body when IsText(body.ContentType) || (body.ContentType is null && message is IHasText):
                if (message is IHasText hasText && !body.IsStreamed)
                    return Frame.Text(hasText.Text);
                return Frame.Text(Encoding.UTF8.GetString((await BytesOfAsync(body, cancellationToken).ConfigureAwait(false)).Span));
            case IHasBody body:
                return new Frame(await BytesOfAsync(body, cancellationToken).ConfigureAwait(false), WebSocketMessageType.Binary);
            case IHasText plain:
                return Frame.Text(plain.Text);
            default:
                return Frame.Text(message.ToText());
        }
    }

    /// <summary>A message's bytes: its body if it has one, or its text as UTF-8.</summary>
    public static async ValueTask<ReadOnlyMemory<byte>> BytesOfAsync(IHasBody body, CancellationToken cancellationToken)
    {
        switch (body)
        {
            case HttpRequest { IsStreamed: false } request:
                return request.Body;
            case HttpResponse { IsStreamed: false } response:
                return response.Body;
            case WebSocketBinary binary:
                return binary.Body;
        }
        await using var stream = body.OpenBody();
        return await Bodies.ReadAsync(stream, -1, Limit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a media type is text of some kind, such as <c>text/plain</c> or <c>application/json</c>.</summary>
    public static bool IsText(string? contentType)
    {
        if (contentType is null || !MediaTypeHeaderValue.TryParse(contentType, out var media))
            return false;
        var type = media.MediaType.Value ?? "";
        return type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || type.EndsWith("/json", StringComparison.OrdinalIgnoreCase) || type.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || type.EndsWith("/xml", StringComparison.OrdinalIgnoreCase) || type.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)
            || type.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
            || type.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
            || media.Charset.HasValue;
    }
}
