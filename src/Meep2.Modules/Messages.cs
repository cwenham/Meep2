using System.Text;
using System.Text.Json;
using Meep2.SDK.Messages;

namespace Meep2.Modules;

/// <summary>What <see cref="Interval"/> emits each time it fires.</summary>
/// <param name="Count">How many times it has fired, counting from 1.</param>
public sealed record Tick(long Count) : Message
{
    /// <summary>The text given with Interval's <c>payload</c> parameter, if any.</summary>
    public string? Payload { get; init; }
}

/// <summary>The contents of a text file, as <see cref="Read"/> emits it.</summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="Text">What the file contains.</param>
public sealed record FileText(string Path, string Text) : Message, IHasText, IHasPath;

/// <summary>An HTTP response with a success status, as <see cref="Fetch"/> emits it.</summary>
public sealed record HttpResponse : Message, IHasText
{
    /// <summary>The address that was fetched.</summary>
    public required string Url { get; init; }

    /// <summary>The status code, such as 200.</summary>
    public required int StatusCode { get; init; }

    /// <summary>The status code's description, such as "OK".</summary>
    public string? ReasonPhrase { get; init; }

    /// <summary>The media type of the body, such as <c>application/json</c>.</summary>
    public string? ContentType { get; init; }

    /// <summary>The response headers. Names are case-insensitive; repeated headers are joined with commas.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>The body as bytes.</summary>
    public required ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>The body as text, decoded with the response's character set (UTF-8 if it doesn't say).</summary>
    public required string Text { get; init; }

    /// <summary>Prints the address and status, leaving out the body.</summary>
    protected override bool PrintMembers(StringBuilder builder)
    {
        base.PrintMembers(builder);
        builder.Append(", Url = ").Append(Url)
            .Append(", StatusCode = ").Append(StatusCode)
            .Append(", ContentType = ").Append(ContentType)
            .Append(", Length = ").Append(Body.Length);
        return true;
    }
}

/// <summary>An HTTP response whose status wasn't a success, as <see cref="Fetch"/> emits it.</summary>
public sealed record HttpErrorMessage : ErrorMessage
{
    /// <summary>The address that was fetched.</summary>
    public required string Url { get; init; }

    /// <summary>The status code, such as 404.</summary>
    public required int StatusCode { get; init; }

    /// <summary>The body as text, which often explains the error.</summary>
    public string? Text { get; init; }
}

/// <summary>
/// A piece of JSON, as <see cref="Extract"/> emits it. In Meeplang its members are read by name for each message, as
/// in <c>msg.minutesAway</c>.
/// </summary>
/// <param name="Value">The JSON value.</param>
public sealed record JsonMessage(JsonElement Value) : Message, IHasText, IHasDynamicMembers
{
    /// <summary>A string's own text, or the JSON for anything else.</summary>
    public string Text => Value.ValueKind == JsonValueKind.String ? Value.GetString()! : Value.GetRawText();

    /// <summary>Reads a member of a JSON object by name. Names are case-sensitive, as in JSON.</summary>
    public bool TryGetMember(string name, out object? value)
    {
        if (Value.ValueKind == JsonValueKind.Object && Value.TryGetProperty(name, out var member))
        {
            value = member;
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>Prints the JSON.</summary>
    protected override bool PrintMembers(StringBuilder builder)
    {
        base.PrintMembers(builder);
        builder.Append(", Value = ").Append(Value.GetRawText());
        return true;
    }
}
