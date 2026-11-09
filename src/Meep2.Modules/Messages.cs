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

/// <summary>An HTTP response with a success status, as Fetch's HTTP inflection emits it.</summary>
/// <remarks>With <c>stream=true</c>, the body is left unread: <see cref="Body"/> and <see cref="Text"/> are empty, and
/// one module can read it with <see cref="OpenBody"/>. The response carries a lease that keeps the connection open
/// until then.</remarks>
public sealed record HttpResponse : Message, IHasText, IHasBody
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

    /// <inheritdoc/>
    public bool IsStreamed => Streamed is not null;

    internal StreamedBody? Streamed { get; init; }

    /// <inheritdoc/>
    public Stream OpenBody() => Streamed?.Open() ?? Bodies.Open(Body);

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

/// <summary>
/// A request that reached Listen's HTTP inflection. It carries a lease on the waiting connection, which Respond
/// answers through. If nothing answers it by the time it and every message made from it have left the pipeline,
/// the runtime sends Listen's default reply (Q50).
/// </summary>
/// <remarks>With <c>stream=true</c> on Listen, the body is left unread: <see cref="Body"/> and <see cref="Text"/> are
/// empty, and one module can read it with <see cref="OpenBody"/> (Q51).</remarks>
public sealed record HttpRequest : Message, IHasText, IHasBody
{
    /// <summary>The method, such as GET or POST.</summary>
    public required string Method { get; init; }

    /// <summary>The whole address that was requested.</summary>
    public required string Url { get; init; }

    /// <summary>The path part of the address, such as <c>/quote/7</c>.</summary>
    public required string Path { get; init; }

    /// <summary>The query's values by name. A name given more than once has its values joined with commas.</summary>
    public required IReadOnlyDictionary<string, string> Query { get; init; }

    /// <summary>The request headers. Names are case-insensitive; repeated headers are joined with commas.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>The media type of the body, such as <c>application/json</c>.</summary>
    public string? ContentType { get; init; }

    /// <summary>The body as bytes, empty if there is none or it's streamed.</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>The body as text, decoded with the request's character set (UTF-8 if it doesn't say).</summary>
    public string Text { get; init; } = "";

    /// <summary>The address and port the request came from.</summary>
    public string? RemoteAddress { get; init; }

    /// <inheritdoc/>
    public bool IsStreamed => Streamed is not null;

    internal StreamedBody? Streamed { get; init; }

    /// <inheritdoc/>
    public Stream OpenBody() => Streamed?.Open() ?? Bodies.Open(Body);

    /// <summary>Prints the method and address, leaving out the body.</summary>
    protected override bool PrintMembers(StringBuilder builder)
    {
        base.PrintMembers(builder);
        builder.Append(", Method = ").Append(Method)
            .Append(", Url = ").Append(Url)
            .Append(", Length = ").Append(Body.Length);
        return true;
    }
}

/// <summary>
/// A message that arrived on a connection to Listen's TCP inflection: a line, or what came before Listen's
/// <c>end</c>, or <c>length</c> bytes. It carries a lease on the connection, which Respond writes back through.
/// </summary>
/// <param name="Text">The message as text, decoded as UTF-8, without what ended it.</param>
public sealed record TcpMessage(string Text) : Message, IHasText
{
    /// <summary>The message as bytes, without what ended it.</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>The address and port the connection came from.</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>A number for the connection, the same for every message from it, which tells connections apart.</summary>
    public long Connection { get; init; }

    /// <summary>Prints the text, the address and the connection number.</summary>
    protected override bool PrintMembers(StringBuilder builder)
    {
        base.PrintMembers(builder);
        builder.Append(", Text = ").Append(Text)
            .Append(", RemoteAddress = ").Append(RemoteAddress)
            .Append(", Connection = ").Append(Connection);
        return true;
    }
}

/// <summary>
/// A client connected to Listen's TCP inflection, emitted with <c>connections=true</c>. It carries a lease on the
/// connection, so Respond can greet the client before it sends anything.
/// </summary>
public sealed record TcpConnected : Message
{
    /// <summary>The address and port the connection came from.</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>A number for the connection, the same for every message from it.</summary>
    public long Connection { get; init; }
}

/// <summary>
/// A connection to Listen's TCP inflection ended, emitted with <c>connections=true</c>. It carries the connection's
/// lease, though the connection may have closed already.
/// </summary>
public sealed record TcpDisconnected : Message
{
    /// <summary>The address and port the connection came from.</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>A number for the connection, the same for every message from it.</summary>
    public long Connection { get; init; }

    /// <summary>Why it ended, such as "The client closed the connection."</summary>
    public required string Reason { get; init; }
}

/// <summary>An HTTP response whose status wasn't a success, as Fetch's HTTP inflection emits it.</summary>
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
