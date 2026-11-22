using System.Text.Json.Nodes;
using Meep2.Daemon;

namespace Meep2.Cli;

/// <summary>
/// The lines of a new text that differ from an old one, counting from 1 (<see cref="Last"/> may be
/// <see cref="First"/> - 1 when lines were only removed), and how many lines longer the new text is.
/// </summary>
internal readonly record struct ChangedLines(int First, int Last, int Shift)
{
    /// <summary>The lines that differ between <paramref name="was"/> and <paramref name="now"/>.</summary>
    public static ChangedLines Between(string was, string now)
    {
        var before = was.Split('\n');
        var after = now.Split('\n');
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)])
            suffix++;
        return new ChangedLines(prefix + 1, after.Length - suffix, after.Length - before.Length);
    }

    /// <summary>The lines as an LSP range, counting from 0.</summary>
    public JsonObject ToRange() => new()
    {
        ["start"] = new JsonObject { ["line"] = First - 1, ["character"] = 0 },
        ["end"] = new JsonObject { ["line"] = Math.Max(Last, First - 1), ["character"] = 0 },
    };

    /// <summary>A graph with the spans in <paramref name="path"/> after these lines moved down (or up) by the lines added (or removed).</summary>
    public PipelineGraph Move(PipelineGraph graph, string path)
    {
        if (Shift == 0)
            return graph;
        var lastBefore = Last - Shift;
        var shift = Shift;
        GraphSpan Move(GraphSpan span) => span.File == path && span.StartLine > lastBefore
            ? span with { StartLine = span.StartLine + shift, EndLine = span.EndLine + shift }
            : span;
        return graph with
        {
            Modules = [.. graph.Modules.Select(m => m with
            {
                Span = Move(m.Span),
                Parameters = [.. m.Parameters.Select(p => p with { Span = Move(p.Span) })],
                Modifiers = [.. m.Modifiers.Select(p => p with { Span = Move(p.Span) })],
                Templates = m.Templates is null ? null : [.. m.Templates.Select(t => t with { Span = Move(t.Span) })],
                Include = m.Include is null ? null : m.Include with { Span = Move(m.Include.Span) },
            })],
        };
    }
}
