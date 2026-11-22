using Meep2.SDK.Messages;

namespace Meep2.Modules;

/// <summary>
/// A connection a Send without an address can reply through: one to Listen.TCP or Listen.WebSocket, or one Send opened
/// itself. Listen.HTTP's requests are answered differently, so they aren't one of these.
/// </summary>
internal interface IConnectionLease
{
    /// <summary>
    /// Writes a reply: <paramref name="text"/>, or the message itself when it's null. <paramref name="end"/> replaces
    /// what ends a message on a TCP connection; <paramref name="final"/> closes the connection after writing.
    /// </summary>
    /// <exception cref="QueueFullException">The other end is too slow, and this reply was dropped.</exception>
    Task ReplyAsync(Message message, string? text, string? end, bool final, CancellationToken cancellationToken);
}
