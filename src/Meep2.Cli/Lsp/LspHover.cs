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
        var word = WordAt(document.Text, parameters["position"]!["line"]!.GetValue<int>(), parameters["position"]!["character"]!.GetValue<int>());
        if (word is null || !workspace.RegistryFor(parameters).TryGetVerb(word, out var inflections))
            return null;
        var markdown = new StringBuilder($"**{word}** ({inflections[0].Role.ToString().ToLowerInvariant()})");
        foreach (var inflection in VerbCatalogue.Describe(inflections))
        {
            markdown.Append($"\n\n`{word}.{inflection.Inflection}`");
            if (inflection.Description is { } description)
                markdown.Append(": ").Append(description);
            foreach (var parameter in inflection.Parameters)
                AppendParameter(markdown, parameter);
        }
        return new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = markdown.ToString() } };
    }

    // The word of letters, digits and _ around a position, counting from 0, or null if the line isn't there.
    private static string? WordAt(string text, int lineNumber, int character)
    {
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
