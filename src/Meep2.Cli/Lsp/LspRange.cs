using System.Text.Json.Nodes;
using Meep2.Meeplang.Syntax;

namespace Meep2.Cli;

/// <summary>Spans as LSP's ranges, which count lines and characters from 0.</summary>
internal static class LspRange
{
    public static JsonObject Of(SourceSpan span) => new()
    {
        ["start"] = new JsonObject { ["line"] = Math.Max(span.Line - 1, 0), ["character"] = Math.Max(span.Column - 1, 0) },
        ["end"] = new JsonObject { ["line"] = Math.Max(span.Line - 1, 0), ["character"] = Math.Max(span.Column - 1 + span.Length, 0) },
    };
}
