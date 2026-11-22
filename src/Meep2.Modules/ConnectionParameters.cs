namespace Meep2.Modules;

/// <summary>
/// Parameters every module that keeps connections open takes: Listen.TCP and Listen.WebSocket, and Send's TCP and
/// WebSocket inflections.
/// </summary>
public abstract record ConnectionParameters
{
    /// <summary>
    /// Also emits a message as each connection opens and another as it ends: <see cref="WebSocketConnected"/> and
    /// <see cref="WebSocketDisconnected"/>, or <see cref="TcpConnected"/> and <see cref="TcpDisconnected"/>.
    /// </summary>
    public bool Connections { get; init; }

    /// <summary>The longest message to read, in bytes. 1 MiB unless set, or, on TCP, <c>length</c> if that's longer.</summary>
    public long MaxSize
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxSize), "The size limit must be at least 1 byte.");
    } = 1024 * 1024;
}
