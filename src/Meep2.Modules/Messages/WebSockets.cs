using System.Text;
using Meep2.SDK.Messages;

namespace Meep2.Modules;

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
