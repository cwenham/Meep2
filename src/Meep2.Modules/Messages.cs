using System.Text;
using System.Text.Json;
using Meep2.SDK.Messages;

namespace Meep2.Modules;

/// <summary>What <see cref="Interval"/> emits each time it fires.</summary>
/// <param name="Count">The tick's number: how many times Interval has fired, counting from 1, or from its <c>count-start</c>.</param>
public sealed record Tick(long Count) : Message
{
    /// <summary>The text given with Interval's <c>payload</c> parameter, if any.</summary>
    public string? Payload { get; init; }
}

/// <summary>Plain text, such as a message sent into a running pipeline as a test (Q72).</summary>
/// <param name="Text">The text.</param>
public sealed record TextMessage(string Text) : Message, IHasText;

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
/// A request that reached Listen's HTTP inflection. It carries a lease on the waiting connection, which a Send without
/// an address answers through. If nothing answers it by the time it and every message made from it have left the
/// pipeline, the runtime sends Listen's default reply (Q50).
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
/// <c>end</c>, or <c>length</c> bytes. It carries a lease on the connection, which a Send without an address replies through.
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
/// connection, so a Send without an address can greet the client before it sends anything.
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

/// <summary>
/// A text message that arrived over a WebSocket: from a client of Listen's WebSocket inflection, or from the server
/// Send's WebSocket inflection connected to. It carries a lease on the connection, which a Send without an address
/// replies through.
/// </summary>
/// <param name="Text">The message's text.</param>
public sealed record WebSocketText(string Text) : Message, IHasText
{
    /// <summary>The WebSocket's address: the one the client asked for, or the server Send connected to.</summary>
    public string? Url { get; init; }

    /// <summary>The address and port the connection came from, for a connection to Listen.</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>A number for the connection, the same for every message from it, which tells connections apart.</summary>
    public long Connection { get; init; }

    /// <summary>Prints the text, the address and the connection number.</summary>
    protected override bool PrintMembers(StringBuilder builder)
    {
        base.PrintMembers(builder);
        builder.Append(", Text = ").Append(Text)
            .Append(", Url = ").Append(Url)
            .Append(", Connection = ").Append(Connection);
        return true;
    }
}

/// <summary>
/// A binary message that arrived over a WebSocket, as <see cref="WebSocketText"/> is a text one. Sent on, it goes as a
/// binary message again.
/// </summary>
public sealed record WebSocketBinary : Message, IHasBody
{
    /// <summary>The message's bytes.</summary>
    public required ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>The WebSocket's address: the one the client asked for, or the server Send connected to.</summary>
    public string? Url { get; init; }

    /// <summary>The address and port the connection came from, for a connection to Listen.</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>A number for the connection, the same for every message from it.</summary>
    public long Connection { get; init; }

    /// <summary>Always <c>application/octet-stream</c>: a WebSocket doesn't say what its bytes are.</summary>
    public string? ContentType => "application/octet-stream";

    /// <inheritdoc/>
    public bool IsStreamed => false;

    /// <inheritdoc/>
    public Stream OpenBody() => Bodies.Open(Body);

    /// <summary>Prints the length, the address and the connection number, leaving out the bytes.</summary>
    protected override bool PrintMembers(StringBuilder builder)
    {
        base.PrintMembers(builder);
        builder.Append(", Length = ").Append(Body.Length)
            .Append(", Url = ").Append(Url)
            .Append(", Connection = ").Append(Connection);
        return true;
    }
}

/// <summary>
/// A WebSocket connection opened, emitted with <c>connections=true</c>: a client connected to Listen, or Send connected
/// to a server. It carries a lease on the connection, so a Send without an address can greet the other end.
/// </summary>
public sealed record WebSocketConnected : Message
{
    /// <summary>The WebSocket's address: the one the client asked for, or the server Send connected to.</summary>
    public string? Url { get; init; }

    /// <summary>The address and port the connection came from, for a connection to Listen.</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>A number for the connection, the same for every message from it.</summary>
    public long Connection { get; init; }

    /// <summary>The path the client asked for, such as <c>/chat/lobby</c>, for a connection to Listen.</summary>
    public string? Path { get; init; }

    /// <summary>The query's values by name, for a connection to Listen, such as a token. Repeated names are joined with commas.</summary>
    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>();

    /// <summary>The headers of the request that opened it, for a connection to Listen, such as its cookies.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// A WebSocket connection ended, emitted with <c>connections=true</c>. It carries the connection's lease, though the
/// connection may have closed already.
/// </summary>
public sealed record WebSocketDisconnected : Message
{
    /// <summary>The WebSocket's address: the one the client asked for, or the server Send connected to.</summary>
    public string? Url { get; init; }

    /// <summary>The address and port the connection came from, for a connection to Listen.</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>A number for the connection, the same for every message from it.</summary>
    public long Connection { get; init; }

    /// <summary>Why it ended, such as "The client closed the connection."</summary>
    public required string Reason { get; init; }

    /// <summary>The close code the other end gave, such as 1000 for a normal close, if it gave one.</summary>
    public int? CloseStatus { get; init; }
}

/// <summary>
/// What Classify emits: the class a message most likely belongs to, and how likely. Its parent is the message that
/// was classified, so <c>fam</c> reaches it. When the tree hasn't learned enough to classify it, <see cref="Class"/>
/// is null and <see cref="Score"/> is 0 (Q144).
/// </summary>
/// <param name="Tree">The classification tree that was used, as named in the pipeline.</param>
/// <param name="Class">The class with the highest score, such as "Science", or null if the message is unclassified.</param>
/// <param name="Score">
/// The probability, from 0 to 1, that the text belongs to <paramref name="Class"/> rather than another class the tree
/// knows, or the classes given with <c>against</c>. Naive Bayes is overconfident, so scores near 1 are common on long
/// texts; compare them with care.
/// </param>
/// <param name="Text">The text that was classified.</param>
public sealed record ClassifiedMessage(string Tree, string? Class, double Score, string Text) : Message, IHasText
{
    /// <summary>Whether a class was found.</summary>
    public bool IsClassified => Class is not null;

    /// <summary>
    /// Every class that was scored, with its probability. Without <c>against</c> they add up to 1; with it, each is
    /// that class's probability against the <c>against</c> classes alone.
    /// </summary>
    public IReadOnlyDictionary<string, double> Scores { get; init; } = new Dictionary<string, double>();

    /// <summary>Prints the tree, the class and the score, leaving out the text.</summary>
    protected override bool PrintMembers(StringBuilder builder)
    {
        base.PrintMembers(builder);
        builder.Append(", Tree = ").Append(Tree)
            .Append(", Class = ").Append(Class)
            .Append(", Score = ").Append(Score.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }
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

/// <summary>An item from a book in the library, as <see cref="Enumerate"/> emits it.</summary>
/// <param name="Book">The book's name, as the library spells it.</param>
/// <param name="Index">The item's place in the book, counting from 0.</param>
/// <param name="Text">The item.</param>
public sealed record BookItem(string Book, long Index, string Text) : Message, IHasText;
