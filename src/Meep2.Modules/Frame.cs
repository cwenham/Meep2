using System.Net.WebSockets;
using System.Text;

namespace Meep2.Modules;

/// <summary>One WebSocket message to send, whole.</summary>
internal readonly record struct Frame(ReadOnlyMemory<byte> Bytes, WebSocketMessageType Type)
{
    public static Frame Text(string text) => new(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text);
}
