using System.Text.Json.Nodes;

namespace Meep2.Cli;

/// <summary>A request that can't be answered; its message goes back to the client, with LSP's code for why, and details.</summary>
internal sealed class LspException(string message, int code = LspException.InvalidParams, JsonNode? details = null) : Exception(message)
{
    // LSP's error codes.
    public const int InvalidParams = -32602;
    public const int ContentModified = -32801;
    public const int RequestFailed = -32803;

    public int Code { get; } = code;

    public JsonNode? Details { get; } = details;
}
