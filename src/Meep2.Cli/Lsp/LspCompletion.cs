using System.Text.Json.Nodes;
using Meep2.Meeplang.Loading;
using Meep2.SDK.Modules;

namespace Meep2.Cli;

/// <summary><c>textDocument/completion</c>: what fits where the cursor is in an open file.</summary>
/// <param name="workspace">The open files and the verbs each can use.</param>
internal sealed class LspCompletion(LspWorkspace workspace)
{
    private static readonly string[] s_modifiers = ["buffer", "bypass", "errors", "no-errors", "expires", "accept-expired", "on-error", "permission", "proof"];
    private static readonly string[] s_defaults = ["buffer", "expires", "on-error", "keep-finished"];
    private static readonly string[] s_keywords = ["defaults", "const", "template", "#include", "#plugin", "#permission"];

    // What fits where the cursor is: a book in a module's quoted value that takes one, a modifier after @, a message
    // type after is, a setting under defaults, or a verb.
    public JsonNode Complete(JsonNode parameters)
    {
        var (line, word) = LineBefore(parameters);
        var registry = workspace.RegistryFor(parameters);
        if (BookValue(line, registry) is { } quote)
            return CompleteBooks(parameters, quote, registry);
        IEnumerable<(string Label, int Kind, string? Detail)> items;
        if (word.StartsWith('@'))
            items = s_modifiers.Select(m => ("@" + m, 14, (string?)"modifier"));
        else if (line.TrimEnd().EndsWith(" is", StringComparison.Ordinal) || line.EndsWith(" is ", StringComparison.Ordinal) || line.EndsWith(" is not ", StringComparison.Ordinal))
            items = registry.MessageTypeNames.Select(t => (t, 7, (string?)"message type"));
        else if (line.Length > 0 && char.IsWhiteSpace(line[0]) && UnderDefaults(parameters))
            items = s_defaults.Select(d => (d, 10, (string?)"default"));
        else
            items = registry.Verbs.Select(v => (v, 3, (string?)Describe(registry, v))).Concat(s_keywords.Select(k => (k, 14, (string?)"keyword")));
        var list = new JsonArray();
        foreach (var (label, kind, detail) in items.OrderBy(i => i.Label, StringComparer.Ordinal))
            list.Add(new JsonObject { ["label"] = label, ["kind"] = kind, ["detail"] = detail });
        return new JsonObject { ["isIncomplete"] = false, ["items"] = list };
    }

    // Where a book's name starts, when the cursor is in the quoted value straight after a verb whose value is a book,
    // as in Enumerate "Coun (Q168).
    private static int? BookValue(string line, ModuleRegistry registry)
    {
        var match = s_bookValue.Match(line);
        if (!match.Success)
            return null;
        var verb = match.Groups["verb"].Value;
        var dot = verb.IndexOf('.', StringComparison.Ordinal);
        IReadOnlyList<ModuleRegistration>? inflections = null;
        if (!registry.TryGetVerb(verb, out inflections) && dot > 0)
        {
            if (!registry.TryGetVerb(verb[..dot], out inflections) && !registry.TryGetPluginVerb(verb[..dot], verb[(dot + 1)..], out inflections))
                inflections = null;
        }
        var takesBook = inflections?.Any(r => r.ParametersType?.GetProperties()
            .Any(p => p.IsDefined(typeof(PositionalAttribute), false) && p.PropertyType == typeof(Book)) == true) == true;
        return takesBook ? match.Groups["name"].Index : null;
    }

    private static readonly System.Text.RegularExpressions.Regex s_bookValue =
        new(@"^\s*(?<verb>[A-Za-z_][\w.]*)\s+""(?<name>[^""]*)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // The library's books, each replacing what's been typed of the name so far, spaces and all.
    private static JsonObject CompleteBooks(JsonNode parameters, int start, ModuleRegistry registry)
    {
        var line = parameters["position"]!["line"]!.GetValue<int>();
        var character = parameters["position"]!["character"]!.GetValue<int>();
        var range = new JsonObject
        {
            ["start"] = new JsonObject { ["line"] = line, ["character"] = start },
            ["end"] = new JsonObject { ["line"] = line, ["character"] = character },
        };
        var list = new JsonArray();
        foreach (var book in registry.Books.Order(StringComparer.OrdinalIgnoreCase))
        {
            list.Add(new JsonObject
            {
                ["label"] = book,
                ["kind"] = 18,
                ["detail"] = "book",
                ["textEdit"] = new JsonObject { ["range"] = range.DeepClone(), ["newText"] = book },
            });
        }
        return new JsonObject { ["isIncomplete"] = false, ["items"] = list };
    }


    private static string Describe(ModuleRegistry registry, string verb) =>
        registry.TryGetVerb(verb, out var inflections) ? $"{inflections[0].Role.ToString().ToLowerInvariant()}: {string.Join(", ", inflections.Select(i => i.Inflection))}" : "module";

    private (string Line, string Word) LineBefore(JsonNode parameters)
    {
        if (workspace.Of(parameters) is not { } document)
            return ("", "");
        var lines = document.Text.Split('\n');
        var lineNumber = parameters["position"]!["line"]!.GetValue<int>();
        if (lineNumber >= lines.Length)
            return ("", "");
        var line = lines[lineNumber][..Math.Min(parameters["position"]!["character"]!.GetValue<int>(), lines[lineNumber].Length)];
        var space = line.LastIndexOf(' ');
        return (line, line[(space + 1)..]);
    }

    // Whether the line at the cursor is in the defaults block.
    private bool UnderDefaults(JsonNode parameters)
    {
        if (workspace.Of(parameters) is not { } document)
            return false;
        var lines = document.Text.Split('\n');
        for (var i = parameters["position"]!["line"]!.GetValue<int>() - 1; i >= 0; i--)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
                continue;
            return line.Trim() == "defaults";
        }
        return false;
    }

}
