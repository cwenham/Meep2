using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Meep2.Daemon;

/// <summary>A request to the daemon's API, whether it came by HTTP or over a WebSocket.</summary>
/// <param name="Method">GET, POST or DELETE.</param>
/// <param name="Parts">The path's parts, unescaped.</param>
/// <param name="Query">The query's values.</param>
/// <param name="Text">A text body, if any.</param>
/// <param name="Json">A JSON body, if any.</param>
/// <param name="Capabilities">What the client may do.</param>
/// <param name="Aborted">Cancelled if the client goes away.</param>
internal sealed record ApiRequest(
    string Method,
    string[] Parts,
    IReadOnlyDictionary<string, string> Query,
    string? Text,
    JsonElement? Json,
    Capabilities Capabilities,
    CancellationToken Aborted);

/// <summary>What the API gives back: an <see cref="Answer"/>, or a stream of <see cref="Events"/>.</summary>
internal abstract record ApiReply;

/// <summary>A status, and JSON if there's anything to say.</summary>
internal sealed record Answer(int Status, Action<Utf8JsonWriter>? Body) : ApiReply
{
    public static Answer Json<T>(int status, T value, JsonTypeInfo<T> type) =>
        new(status, writer => JsonSerializer.Serialize(writer, value, type));
}

/// <summary>Events until the stream ends or the client stops listening. The last is called end, unless the client stopped it.</summary>
internal sealed record Events(Func<CancellationToken, IAsyncEnumerable<ApiEvent>> Stream) : ApiReply;

/// <summary>A subscription (<c>POST /subscribe</c>), which only a WebSocket can carry.</summary>
internal sealed record SubscribeReply(Subscriptions.Subscription Subscription) : ApiReply;

/// <summary>One event in a stream: its name, and its data as JSON.</summary>
internal readonly record struct ApiEvent(string Name, Action<Utf8JsonWriter> Data);
