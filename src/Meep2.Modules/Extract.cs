using System.Text.Json;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Reads a message's content as JSON and emits a <see cref="JsonMessage"/> for each value a JSONPath query matches.
/// In Meeplang: <c>Extract "$.departures[*]"</c>.
/// </summary>
[Verb("Extract")]
[Emits(typeof(JsonMessage))]
public sealed class Extract(ExtractParameters parameters) : AInflection<IHasText>
{
    /// <inheritdoc/>
    protected override ValueTask HandleAsync(IHasText message, ModuleContext context, CancellationToken cancellationToken)
    {
        JsonElement root;
        try
        {
            root = message switch
            {
                JsonMessage json => json.Value,
                HttpResponse response => ParseJson(response.Body),
                _ => ParseJson(message.Text),
            };
        }
        catch (JsonException exception)
        {
            context.EmitError($"Extract needs JSON, but couldn't read it: {exception.Message}", exception);
            return ValueTask.CompletedTask;
        }

        foreach (var match in parameters.Path.Select(root))
            context.Emit(new JsonMessage(match));
        return ValueTask.CompletedTask;
    }

    // Clone so the result doesn't depend on the document's pooled memory, which is returned on dispose.
    private static JsonElement ParseJson(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>Parameters for <see cref="Extract"/>.</summary>
public sealed record ExtractParameters
{
    /// <summary>The JSONPath query, such as <c>$.departures[*]</c>.</summary>
    [Positional]
    public required JsonPath Path { get; init; }
}
