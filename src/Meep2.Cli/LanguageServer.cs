using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meep2.Meeplang.Hosting;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Modules;
using Meep2.Plugins;
using Meep2.SDK.Modules;

namespace Meep2.Cli;

/// <summary>
/// <c>meep lsp</c>: a Language Server Protocol server for Meeplang, over standard input and output, for the VSCode
/// extension and any other editor that speaks LSP (step 9). It checks a file as it's edited with Meeplang's own parser
/// and loader, so an editor shows the same mistakes <c>meep run</c> would, and offers completions and hovers.
/// </summary>
/// <remarks>
/// A file is loaded against the core modules and the plugins its <c>#plugin</c> lines name, so wrong verbs, parameters
/// and expressions show too. Plugins come only from Meep's cache, with nothing downloaded, and only those
/// <c>meep run</c> would trust without <c>--dev</c>, so opening a file can't load code you haven't trusted (Q138). A
/// plugin that isn't there is reported on its line, and the rest of the file is only parsed.
/// </remarks>
/// <param name="input">What the editor sends.</param>
/// <param name="output">Where replies go.</param>
/// <param name="plugins">Where plugins are cached, and the keys you trust; without it, a file with plugins is only parsed.</param>
internal sealed class LanguageServer(Stream input, Stream output, PluginOptions? plugins = null)
{
    private static readonly string[] s_modifiers = ["buffer", "bypass", "errors", "no-errors", "expires", "accept-expired", "on-error"];
    private static readonly string[] s_defaults = ["buffer", "expires", "on-error", "keep-finished"];
    private static readonly string[] s_keywords = ["defaults", "const", "template", "#include", "#plugin"];

    private readonly Dictionary<string, string> _documents = new(StringComparer.Ordinal);
    private readonly ModuleRegistry _registry = new ModuleRegistry().AddAssembly(CoreModules.Assembly);
    private readonly PluginInstaller? _installer = plugins is null ? null : new PluginInstaller(plugins with { Offline = true, DeveloperMode = false });
    private readonly PluginCatalog _catalog = new();

    // Each open file's plugins, kept loaded while its #plugin lines stay the same.
    private readonly Dictionary<string, (string Lines, InstalledPlugins Installed)> _loaded = new(StringComparer.Ordinal);
    private bool _shuttingDown;

    /// <summary>Serves requests until the client says to exit, or closes the stream. Returns the exit code.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        while (await ReadAsync(cancellationToken).ConfigureAwait(false) is { } message)
        {
            var method = message["method"]?.GetValue<string>();
            var id = message["id"];
            var parameters = message["params"];
            switch (method)
            {
                case "initialize":
                    await RespondAsync(id, new JsonObject
                    {
                        ["capabilities"] = new JsonObject
                        {
                            ["textDocumentSync"] = 1, // the whole text, on each change
                            ["completionProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray("@", " ", "#", "\"") },
                            ["hoverProvider"] = true,
                        },
                        ["serverInfo"] = new JsonObject { ["name"] = "meep" },
                    }, cancellationToken).ConfigureAwait(false);
                    break;
                case "shutdown":
                    _shuttingDown = true;
                    await RespondAsync(id, null, cancellationToken).ConfigureAwait(false);
                    break;
                case "exit":
                    foreach (var uri in _loaded.Keys.ToList())
                        Unload(uri);
                    return _shuttingDown ? 0 : 1;
                case "textDocument/didOpen":
                    await ChangedAsync(parameters!["textDocument"]!["uri"]!.GetValue<string>(), parameters["textDocument"]!["text"]!.GetValue<string>(), cancellationToken).ConfigureAwait(false);
                    break;
                case "textDocument/didChange":
                    var changes = parameters!["contentChanges"]!.AsArray();
                    if (changes.Count > 0)
                        await ChangedAsync(parameters["textDocument"]!["uri"]!.GetValue<string>(), changes[^1]!["text"]!.GetValue<string>(), cancellationToken).ConfigureAwait(false);
                    break;
                case "textDocument/didClose":
                    var closed = parameters!["textDocument"]!["uri"]!.GetValue<string>();
                    _documents.Remove(closed);
                    Unload(closed);
                    await PublishAsync(closed, [], cancellationToken).ConfigureAwait(false);
                    break;
                case "textDocument/completion":
                    await RespondAsync(id, Complete(parameters!), cancellationToken).ConfigureAwait(false);
                    break;
                case "textDocument/hover":
                    await RespondAsync(id, Hover(parameters!), cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    if (id is not null)
                        await RespondErrorAsync(id, -32601, $"meep lsp doesn't handle {method}.", cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        return 0;
    }

    /// <summary>The problems Meeplang finds in a file's text, by the file they're in.</summary>
    /// <param name="uri">The file as the editor names it, to keep its plugins loaded between changes.</param>
    /// <param name="path">The file's path.</param>
    /// <param name="text">Its text, as it is in the editor.</param>
    /// <param name="cancellationToken">Stops loading plugins.</param>
    public async Task<IReadOnlyList<Diagnostic>> CheckAsync(string uri, string path, string text, CancellationToken cancellationToken)
    {
        var syntax = MeeplangParser.Parse(new SourceFile(path, text));
        if (syntax.HasErrors)
            return syntax.Diagnostics;
        var registry = _registry.Clone();
        registry.Add<WriteLine, WriteLineParameters>("WriteLine", parameters => new WriteLine(parameters, TextWriter.Null));
        var warnings = new List<Diagnostic>();
        if (syntax.Plugins.Count > 0)
        {
            if (_installer is null)
                return syntax.Diagnostics;
            var lines = string.Join("\n", syntax.Plugins.Select(p => $"{p} as {p.Alias}"));
            if (!_loaded.TryGetValue(uri, out var loaded) || loaded.Lines != lines)
            {
                Unload(uri);
                loaded = (lines, await Install.InstallAllAsync(syntax, registry, _installer, _catalog, saveLock: false, cancellationToken).ConfigureAwait(false));
                _loaded[uri] = loaded;
            }
            if (loaded.Installed.Problems.Count > 0)
                return [.. syntax.Diagnostics, .. loaded.Installed.Warnings, .. loaded.Installed.Problems];
            registry = loaded.Installed.Registry;
            warnings.AddRange(loaded.Installed.Warnings);
        }
        return [.. warnings, .. new PipelineLoader(registry).Load(syntax).Diagnostics];
    }

    // Lets go of a file's plugins.
    private void Unload(string uri)
    {
        if (_loaded.Remove(uri, out var loaded))
            Build.Release(loaded.Installed.Holds);
    }

    // The verbs a file can use: the core modules', and its plugins' once they've loaded.
    private ModuleRegistry RegistryFor(JsonNode parameters) =>
        _loaded.TryGetValue(parameters["textDocument"]!["uri"]!.GetValue<string>(), out var loaded) && loaded.Installed.Problems.Count == 0
            ? loaded.Installed.Registry
            : _registry;

    private async Task ChangedAsync(string uri, string text, CancellationToken cancellationToken)
    {
        _documents[uri] = text;
        var path = PathOf(uri);
        var diagnostics = await CheckAsync(uri, path, text, cancellationToken).ConfigureAwait(false);
        await PublishAsync(uri, [.. diagnostics.Where(d => d.Span.File is null || d.Span.File == path)], cancellationToken).ConfigureAwait(false);
        foreach (var included in diagnostics.Where(d => d.Span.File is { } file && file != path).GroupBy(d => d.Span.File!))
            await PublishAsync(new Uri(included.Key).ToString(), [.. included], cancellationToken).ConfigureAwait(false);
    }

    private Task PublishAsync(string uri, IReadOnlyList<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var list = new JsonArray();
        foreach (var diagnostic in diagnostics)
        {
            list.Add(new JsonObject
            {
                ["range"] = Range(diagnostic.Span),
                ["severity"] = diagnostic.IsError ? 1 : 2,
                ["source"] = "meep",
                ["message"] = diagnostic.Message,
            });
        }
        return NotifyAsync("textDocument/publishDiagnostics", new JsonObject { ["uri"] = uri, ["diagnostics"] = list }, cancellationToken);
    }

    // What fits where the cursor is: a book in a module's quoted value that takes one, a modifier after @, a message
    // type after is, a setting under defaults, or a verb.
    private JsonNode Complete(JsonNode parameters)
    {
        var (line, word) = LineBefore(parameters);
        var registry = RegistryFor(parameters);
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

    // A verb's inflections and their parameters, for the word under the cursor.
    private JsonNode? Hover(JsonNode parameters)
    {
        if (!_documents.TryGetValue(parameters["textDocument"]!["uri"]!.GetValue<string>(), out var text))
            return null;
        var lineNumber = parameters["position"]!["line"]!.GetValue<int>();
        var character = parameters["position"]!["character"]!.GetValue<int>();
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
        var word = line[start..end];
        if (!RegistryFor(parameters).TryGetVerb(word, out var inflections))
            return null;
        var markdown = new StringBuilder($"**{word}**");
        foreach (var inflection in inflections)
        {
            markdown.Append($"\n\n`{word}.{inflection.Inflection}`");
            if (inflection.ParametersType is { } type)
            {
                var names = type.GetProperties().Where(p => p.CanWrite).Select(p => Kebab(p.Name));
                markdown.Append(": ").Append(string.Join(", ", names));
            }
        }
        return new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = markdown.ToString() } };
    }

    private static string Describe(ModuleRegistry registry, string verb) =>
        registry.TryGetVerb(verb, out var inflections) ? $"{string.Join(", ", inflections.Select(i => i.Inflection))}" : "module";

    private (string Line, string Word) LineBefore(JsonNode parameters)
    {
        if (!_documents.TryGetValue(parameters["textDocument"]!["uri"]!.GetValue<string>(), out var text))
            return ("", "");
        var lines = text.Split('\n');
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
        if (!_documents.TryGetValue(parameters["textDocument"]!["uri"]!.GetValue<string>(), out var text))
            return false;
        var lines = text.Split('\n');
        for (var i = parameters["position"]!["line"]!.GetValue<int>() - 1; i >= 0; i--)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
                continue;
            return line.Trim() == "defaults";
        }
        return false;
    }

    private static JsonObject Range(SourceSpan span) => new()
    {
        ["start"] = new JsonObject { ["line"] = Math.Max(span.Line - 1, 0), ["character"] = Math.Max(span.Column - 1, 0) },
        ["end"] = new JsonObject { ["line"] = Math.Max(span.Line - 1, 0), ["character"] = Math.Max(span.Column - 1 + span.Length, 0) },
    };

    private static string PathOf(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : uri;

    private static string Kebab(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private Task RespondAsync(JsonNode? id, JsonNode? result, CancellationToken cancellationToken) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result }, cancellationToken);

    private Task RespondErrorAsync(JsonNode id, int code, string message, CancellationToken cancellationToken) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, cancellationToken);

    private Task NotifyAsync(string method, JsonNode parameters, CancellationToken cancellationToken) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, cancellationToken);

    // A message, framed as LSP frames them: a Content-Length header, a blank line, then the JSON.
    private async Task WriteAsync(JsonNode message, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonNode?> ReadAsync(CancellationToken cancellationToken)
    {
        var length = -1;
        while (true)
        {
            var line = await ReadHeaderLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                return null;
            if (line.Length == 0)
                break;
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (length < 0)
            return null;
        var body = new byte[length];
        await input.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(body);
    }

    private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await input.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
                return line.Length == 0 ? null : line.ToString();
            if (one[0] == '\n')
                return line.ToString().TrimEnd('\r');
            line.Append((char)one[0]);
        }
    }
}
