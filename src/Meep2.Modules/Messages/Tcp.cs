using System.Text;
using Meep2.SDK.Messages;

namespace Meep2.Modules;

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
