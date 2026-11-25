using System.Text;
using System.Text.Json.Nodes;
using Meep2.Meeplang.Loading;

namespace Meep2.Cli;

/// <summary><c>textDocument/hover</c>: a verb's inflections and their parameters, for the word under the cursor.</summary>
/// <param name="workspace">The open files and the verbs each can use.</param>
internal sealed class LspHover(LspWorkspace workspace)
{
    public JsonNode? Hover(JsonNode parameters)
    {
        if (workspace.Of(parameters) is not { } document)
            return null;
        var lineNumber = parameters["position"]!["line"]!.GetValue<int>();
        var character = parameters["position"]!["character"]!.GetValue<int>();
        var word = WordAt(document.Text, lineNumber, character, out var at);
        if (word is "permission" && at > 0 && document.Text.Split('\n')[lineNumber][at - 1] == '@')
            return Markdown(Gate(document.Text.Split('\n')[lineNumber], workspace.RegistryFor(parameters)));
        if (word is null || !workspace.RegistryFor(parameters).TryGetVerb(word, out var inflections))
            return null;
        var markdown = new StringBuilder($"**{word}** ({inflections[0].Role.ToString().ToLowerInvariant()})");
        foreach (var inflection in VerbCatalogue.Describe(inflections))
        {
            markdown.Append($"\n\n`{word}.{inflection.Inflection}`");
            if (inflection.Description is { } description)
                markdown.Append(": ").Append(description);
            if (inflection.Requires.Count > 0)
                markdown.Append($"\n\nAlways needs {string.Join(" and ", inflection.Requires.Select(p => $"`{p}`"))}, which a pipeline can't remove.");
            foreach (var parameter in inflection.Parameters)
                AppendParameter(markdown, parameter);
        }
        return Markdown(markdown.ToString());
    }

    private static JsonObject Markdown(string text) =>
        new() { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = text } };

    // What @permission does, and the gate on this line: its @permission names and what its verb always needs.
    private static string Gate(string line, ModuleRegistry registry)
    {
        var markdown = new StringBuilder(
            "**@permission** (modifier): a gate. A message reaches this module only if every line of its family passes " +
            "through a verified credential holding the permission; otherwise it becomes a `PermissionDenied` error, which " +
            "follows the error policy. Write it more than once to need several. On a source, the pipeline must hold it to start.");
        var written = System.Text.RegularExpressions.Regex.Matches(line, "@permission=(\"[^\"]*\"|[^\\s@]+)")
            .Select(m => m.Groups[1].Value.Trim('"'))
            .ToList();
        var verb = System.Text.RegularExpressions.Regex.Match(line, "^\\s*\\+?\\s*([A-Za-z_][A-Za-z0-9_.]*)").Groups[1].Value;
        var fixedNeeds = verb.Length > 0 && registry.TryGetVerb(verb, out var inflections)
            ? inflections.SelectMany(i => i.RequiredPermissions.Select(p => p.ToString())).Distinct().ToList()
            : [];
        if (written.Count + fixedNeeds.Count > 0)
        {
            markdown.Append("\n\nNeeds here: ");
            markdown.AppendJoin(", ", written.Select(p => $"`{p}`").Concat(fixedNeeds.Where(p => !written.Contains(p)).Select(p => $"`{p}` (from {verb}, can't be removed)")));
        }
        return markdown.ToString();
    }

    // The word of letters, digits and _ around a position, counting from 0, and where it starts, or null if the line
    // isn't there.
    private static string? WordAt(string text, int lineNumber, int character, out int at)
    {
        at = 0;
        var lines = text.Split('\n');
        if (lineNumber >= lines.Length)
            return null;
        var line = lines[lineNumber];
        var start = Math.Min(character, line.Length);
        while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_'))
            start--;
        var end = Math.Min(character, line.Length);
        while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
            end++;
        at = start;
        return line[start..end];
    }

    private static void AppendParameter(StringBuilder markdown, ParameterEntry parameter)
    {
        markdown.Append($"\n- `{parameter.Name}` {parameter.Type}");
        if (parameter.Choices is { } choices)
            markdown.Append(" (").Append(string.Join(", ", choices)).Append(')');
        if (parameter.PerMessage)
            markdown.Append(", per message");
        if (parameter.Positional)
            markdown.Append(", after the verb");
        if (parameter.Required)
            markdown.Append(", required");
        else if (parameter.Default is { } value)
            markdown.Append($", default `{value}`");
        if (parameter.Description is { } about)
            markdown.Append(". ").Append(about);
    }
}
