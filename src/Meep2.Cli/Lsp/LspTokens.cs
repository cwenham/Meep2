using System.Text.Json.Nodes;
using Meep2.Meeplang.Editing;

namespace Meep2.Cli;

/// <summary>
/// <c>textDocument/semanticTokens/full</c>, and <c>/full/delta</c> when the client has the last result: Meep's parser's
/// colours, as LSP's relative positions.
/// </summary>
/// <param name="workspace">The open files and the verbs each can use.</param>
internal sealed class LspTokens(LspWorkspace workspace)
{
    private int _results;

    /// <summary>The tokens of the file a request names, whole, or as the edits to <paramref name="previous"/> if that's the last result.</summary>
    public JsonNode? Tokens(JsonNode parameters, string? previous)
    {
        if (workspace.Of(parameters) is not { } document)
            return null;
        var data = Encode(document);
        var id = (++_results).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var last = document.Tokens;
        document.Tokens = (id, [.. data]);
        if (previous is not null && last is { } before && before.Id == previous)
            return new JsonObject { ["resultId"] = id, ["edits"] = Edits(before.Data, data) };
        return new JsonObject { ["resultId"] = id, ["data"] = new JsonArray([.. data.Select(n => (JsonNode)n)]) };
    }

    // Each token as five numbers: its line and column from the one before, its length, type and modifiers.
    private List<int> Encode(LspDocument document)
    {
        var syntax = document.Syntax ?? workspace.Parse(document.Path, document.Text);
        var data = new List<int>();
        int line = 1, column = 1;
        foreach (var token in Highlighter.Tokens(syntax, document.Registry))
        {
            data.Add(token.Line - line);
            data.Add(token.Line == line ? token.Column - column : token.Column - 1);
            data.Add(token.Length);
            data.Add(token.Type);
            data.Add(token.Modifiers);
            (line, column) = (token.Line, token.Column);
        }
        return data;
    }

    // The one edit, if any, that makes old into data: what lies between their common start and their common end.
    private static JsonArray Edits(int[] old, List<int> data)
    {
        var start = 0;
        while (start < old.Length && start < data.Count && old[start] == data[start])
            start++;
        var end = 0;
        while (end < old.Length - start && end < data.Count - start && old[^(end + 1)] == data[^(end + 1)])
            end++;
        var edits = new JsonArray();
        if (start < old.Length - end || start < data.Count - end)
        {
            edits.Add(new JsonObject
            {
                ["start"] = start,
                ["deleteCount"] = old.Length - end - start,
                ["data"] = new JsonArray([.. data.Skip(start).Take(data.Count - end - start).Select(n => (JsonNode)n)]),
            });
        }
        return edits;
    }
}
