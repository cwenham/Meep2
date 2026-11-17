using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Meep2.Daemon;
using Meep2.Meeplang.Editing;
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
/// and loader, so an editor shows the same mistakes <c>meep run</c> would, and offers completions, hovers and semantic
/// tokens. For tools that draw pipelines, it adds <c>meep/graph</c>, <c>meep/setBase</c> and <c>meep/verbs</c>:
/// see <see cref="Graph"/>, <see cref="SetBaseAsync"/> and <see cref="Verbs"/>.
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

    private readonly Dictionary<string, Document> _documents = new(StringComparer.Ordinal);
    private readonly ModuleRegistry _registry = new ModuleRegistry().AddAssembly(CoreModules.Assembly);
    private readonly PluginInstaller? _installer = plugins is null ? null : new PluginInstaller(plugins with { Offline = true, DeveloperMode = false });
    private readonly PluginCatalog _catalog = new();

    // Each open file's plugins, kept loaded while its #plugin lines stay the same.
    private readonly Dictionary<string, (string Lines, InstalledPlugins Installed)> _loaded = new(StringComparer.Ordinal);
    private bool _shuttingDown;
    private int _tokenResults;

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
                            ["semanticTokensProvider"] = new JsonObject
                            {
                                ["legend"] = new JsonObject
                                {
                                    ["tokenTypes"] = new JsonArray([.. Highlighter.Types.Select(t => (JsonNode)t)]),
                                    ["tokenModifiers"] = new JsonArray([.. Highlighter.Modifiers.Select(m => (JsonNode)m)]),
                                },
                                ["full"] = new JsonObject { ["delta"] = true },
                                ["range"] = false,
                            },
                            ["experimental"] = new JsonObject
                            {
                                ["meep"] = new JsonObject { ["graph"] = true, ["setBase"] = true, ["verbs"] = true },
                            },
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
                    await ChangedAsync(
                        parameters!["textDocument"]!["uri"]!.GetValue<string>(),
                        parameters["textDocument"]!["text"]!.GetValue<string>(),
                        parameters["textDocument"]!["version"]?.GetValue<int>() ?? 0,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "textDocument/didChange":
                    var changes = parameters!["contentChanges"]!.AsArray();
                    if (changes.Count > 0)
                    {
                        await ChangedAsync(
                            parameters["textDocument"]!["uri"]!.GetValue<string>(),
                            changes[^1]!["text"]!.GetValue<string>(),
                            parameters["textDocument"]!["version"]?.GetValue<int>() ?? 0,
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;
                case "textDocument/didClose":
                    var closed = parameters!["textDocument"]!["uri"]!.GetValue<string>();
                    _documents.Remove(closed);
                    Unload(closed);
                    Unload(closed + BaseSuffix);
                    await PublishAsync(closed, [], cancellationToken).ConfigureAwait(false);
                    break;
                case "textDocument/semanticTokens/full":
                    await RespondAsync(id, SemanticTokens(parameters!, null), cancellationToken).ConfigureAwait(false);
                    break;
                case "textDocument/semanticTokens/full/delta":
                    await RespondAsync(id, SemanticTokens(parameters!, parameters!["previousResultId"]?.GetValue<string>()), cancellationToken).ConfigureAwait(false);
                    break;
                case "meep/graph":
                    await RespondAsync(id, Graph(parameters!), cancellationToken).ConfigureAwait(false);
                    break;
                case "meep/setBase":
                    try
                    {
                        await RespondAsync(id, await SetBaseAsync(parameters!, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                    }
                    catch (LspException exception)
                    {
                        await RespondErrorAsync(id!, -32602, exception.Message, cancellationToken).ConfigureAwait(false);
                    }
                    break;
                case "meep/verbs":
                    await RespondAsync(id, Verbs(parameters), cancellationToken).ConfigureAwait(false);
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
        if (!_documents.TryGetValue(uri, out var document))
            _documents[uri] = document = new Document(uri, path);
        document.Text = text;
        return await AnalyseAsync(document, cancellationToken).ConfigureAwait(false);
    }

    // Parses a document, loads it with the verbs it can use, and works out its graph if it loads.
    private async Task<IReadOnlyList<Diagnostic>> AnalyseAsync(Document document, CancellationToken cancellationToken)
    {
        var syntax = Parse(document.Path, document.Text);
        document.Syntax = syntax;
        if (syntax.HasErrors)
        {
            document.Loaded = false;
            return syntax.Diagnostics;
        }
        var (registry, problems) = await RegistryForAsync(document.Uri, syntax, cancellationToken).ConfigureAwait(false);
        document.Registry = registry ?? document.Registry;
        if (registry is null)
        {
            document.Loaded = false;
            return problems;
        }
        var update = document.Draft.Update(syntax, registry);
        document.Loaded = update.Loaded;
        if (update.Loaded)
            Record(document, update.Shape!, update.Renamed, document.Version, document.Text);
        return [.. problems, .. update.Diagnostics];
    }

    // A file parsed, with the files it includes as they are in the editor, if they're open, or else on disk.
    private PipelineSyntax Parse(string path, string text) =>
        MeeplangParser.Parse(new SourceFile(path, text), included =>
            _documents.Values.FirstOrDefault(d => d.Path == included) is { } open ? open.Text
            : File.Exists(included) ? File.ReadAllText(included)
            : null);

    // The verbs a parsed file can use: the core modules', and the plugins its #plugin lines name, loaded from the
    // cache and kept loaded under the key while the lines stay the same. Null, with the problems, if a plugin can't
    // be loaded; then the file is only parsed. Warnings come back either way.
    private async Task<(ModuleRegistry? Registry, IReadOnlyList<Diagnostic> Warnings)> RegistryForAsync(string key, PipelineSyntax syntax, CancellationToken cancellationToken)
    {
        var registry = _registry.Clone();
        registry.Add<WriteLine, WriteLineParameters>("WriteLine", parameters => new WriteLine(parameters, TextWriter.Null));
        if (syntax.Plugins.Count == 0)
        {
            Unload(key);
            return (registry, []);
        }
        if (_installer is null)
            return (null, syntax.Diagnostics);
        var lines = string.Join("\n", syntax.Plugins.Select(p => $"{p} as {p.Alias}"));
        if (!_loaded.TryGetValue(key, out var loaded) || loaded.Lines != lines)
        {
            Unload(key);
            loaded = (lines, await Install.InstallAllAsync(syntax, registry, _installer, _catalog, saveLock: false, cancellationToken).ConfigureAwait(false));
            _loaded[key] = loaded;
        }
        if (loaded.Installed.Problems.Count > 0)
            return (null, [.. syntax.Diagnostics, .. loaded.Installed.Warnings, .. loaded.Installed.Problems]);
        return (loaded.Installed.Registry, loaded.Installed.Warnings);
    }

    // Lets go of a file's plugins.
    private void Unload(string uri)
    {
        if (_loaded.Remove(uri, out var loaded))
            Build.Release(loaded.Installed.Holds);
    }

    // The verbs a file can use: the core modules', and its plugins' once they've loaded.
    private ModuleRegistry RegistryFor(JsonNode? parameters) =>
        parameters?["textDocument"]?["uri"]?.GetValue<string>() is { } uri
        && _loaded.TryGetValue(uri, out var loaded) && loaded.Installed.Problems.Count == 0
            ? loaded.Installed.Registry
            : _registry;

    private async Task ChangedAsync(string uri, string text, int version, CancellationToken cancellationToken)
    {
        if (!_documents.TryGetValue(uri, out var document))
            _documents[uri] = document = new Document(uri, PathOf(uri));
        document.Text = text;
        document.Version = version;
        await CheckAndPublishAsync(document, cancellationToken).ConfigureAwait(false);

        // Files that include this one change with it: a template edited here changes the pipelines that use it.
        foreach (var including in _documents.Values.Where(d => d != document && d.Syntax?.Included.Any(i => i.Source.Path == document.Path) == true).ToList())
            await CheckAndPublishAsync(including, cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckAndPublishAsync(Document document, CancellationToken cancellationToken)
    {
        var path = document.Path;
        var diagnostics = await AnalyseAsync(document, cancellationToken).ConfigureAwait(false);
        document.Problems = [.. diagnostics.Where(d => d.IsError)];
        await PublishAsync(document.Uri, [.. diagnostics.Where(d => d.Span.File is null || d.Span.File == path)], cancellationToken).ConfigureAwait(false);
        foreach (var included in diagnostics.Where(d => d.Span.File is { } file && file != path).GroupBy(d => d.Span.File!))
            await PublishAsync(new Uri(included.Key).ToString(), [.. included], cancellationToken).ConfigureAwait(false);
        await GraphChangedAsync(document, cancellationToken).ConfigureAwait(false);
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
        if (!_documents.TryGetValue(parameters["textDocument"]!["uri"]!.GetValue<string>(), out var document))
            return null;
        var text = document.Text;
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
        var markdown = new StringBuilder($"**{word}** ({inflections[0].Role.ToString().ToLowerInvariant()})");
        foreach (var inflection in VerbCatalogue.Describe(inflections))
        {
            markdown.Append($"\n\n`{word}.{inflection.Inflection}`");
            if (inflection.Description is { } description)
                markdown.Append(": ").Append(description);
            foreach (var parameter in inflection.Parameters)
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
        return new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = markdown.ToString() } };
    }

    private static string Describe(ModuleRegistry registry, string verb) =>
        registry.TryGetVerb(verb, out var inflections) ? $"{inflections[0].Role.ToString().ToLowerInvariant()}: {string.Join(", ", inflections.Select(i => i.Inflection))}" : "module";

    private (string Line, string Word) LineBefore(JsonNode parameters)
    {
        if (!_documents.TryGetValue(parameters["textDocument"]!["uri"]!.GetValue<string>(), out var document))
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
        if (!_documents.TryGetValue(parameters["textDocument"]!["uri"]!.GetValue<string>(), out var document))
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

    // The key a document's base keeps its plugins loaded under, beside the document's own.
    private const string BaseSuffix = "#base";

    // Keeps a document's new graph: its version counts up when anything in it changes, as the daemon's does.
    private static void Record(Document document, PipelineShape shape, IReadOnlyDictionary<string, string> renamed, int builtFrom, string text)
    {
        var graph = Graphs.Describe(document.BaseName ?? System.IO.Path.GetFileNameWithoutExtension(document.Path), document.Path, shape, version: 0);
        var json = JsonSerializer.Serialize(graph, DaemonJson.Default.PipelineGraph);
        if (json != document.GraphJson)
        {
            document.GraphJson = json;
            document.GraphVersion++;
            document.Renamed = renamed;
        }
        document.Graph = graph with { Version = document.GraphVersion };
        document.GraphFrom = builtFrom;
        document.GraphText = text;
    }

    /// <summary>
    /// <c>meep/graph</c>: an open document's pipeline as a graph, the same <see cref="PipelineGraph"/> the daemon's
    /// <c>GET /pipelines/{name}/graph</c> gives, with the document's version. With <c>subscribe: true</c>, a
    /// <c>meep/graphChanged</c> notification with the same answer follows each change to it, until
    /// <c>subscribe: false</c> or the document is closed.
    /// </summary>
    /// <remarks>
    /// <para>The answer: <c>{ uri, version, current, graph, builtFrom, problems, changed, renamed, base }</c>.</para>
    /// <list type="bullet">
    /// <item><c>version</c> is the document's version now, from <c>didOpen</c> and <c>didChange</c>.</item>
    /// <item>
    /// <c>graph</c> is the graph of the last text that loaded, or null if none has: <c>builtFrom</c> is that text's
    /// version, and <c>current</c> says whether it's this one. Its spans count from 1, as the daemon's do. When the
    /// text no longer loads, the spans in lines before and after the lines that changed are moved to where those
    /// lines are now; <c>changed</c> is the lines that differ from the text it was built from, whose modules may have
    /// moved.
    /// </item>
    /// <item><c>problems</c> are the errors that stop the text loading now, with LSP ranges, counting from 0.</item>
    /// <item>
    /// <c>renamed</c> maps keys from the graph's last version to this one, for modules whose key changed (only after
    /// <c>meep/setBase</c>, or when the text's change makes a module match one of the base's). Omitted when empty.
    /// </item>
    /// <item><c>base</c> is the running pipeline the keys are matched with (<see cref="SetBaseAsync"/>), if any.</item>
    /// </list>
    /// </remarks>
    private JsonNode? Graph(JsonNode parameters)
    {
        if (!_documents.TryGetValue(parameters["textDocument"]!["uri"]!.GetValue<string>(), out var document))
            return null;
        var answer = Answer(document);
        if (parameters["subscribe"]?.GetValue<bool>() is { } subscribe)
        {
            document.Subscribed = subscribe;
            document.Sent = answer.ToJsonString();
        }
        return answer;
    }

    private Task GraphChangedAsync(Document document, CancellationToken cancellationToken)
    {
        if (!document.Subscribed)
            return Task.CompletedTask;
        var answer = Answer(document);
        var json = answer.ToJsonString();
        if (json == document.Sent)
            return Task.CompletedTask;
        document.Sent = json;
        return NotifyAsync("meep/graphChanged", answer, cancellationToken);
    }

    private static JsonObject Answer(Document document)
    {
        var current = document.Loaded && document.GraphFrom == document.Version;
        var graph = document.Graph;
        JsonObject? changed = null;
        if (graph is not null && !current && document.GraphText is { } was && was != document.Text)
        {
            var (first, last, shift) = Changed(was, document.Text);
            graph = Shift(graph, document.Path, first, last - shift, shift);
            changed = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = first - 1, ["character"] = 0 },
                ["end"] = new JsonObject { ["line"] = Math.Max(last, first - 1), ["character"] = 0 },
            };
        }
        var problems = new JsonArray();
        foreach (var problem in document.Problems)
        {
            problems.Add(new JsonObject
            {
                ["uri"] = problem.Span.File is { } file && file != document.Path ? new Uri(file).ToString() : document.Uri,
                ["range"] = Range(problem.Span),
                ["message"] = problem.Message,
            });
        }
        var answer = new JsonObject
        {
            ["uri"] = document.Uri,
            ["version"] = document.Version,
            ["current"] = current,
            ["graph"] = graph is null ? null : JsonSerializer.SerializeToNode(graph, DaemonJson.Default.PipelineGraph),
            ["builtFrom"] = graph is null ? null : document.GraphFrom,
            ["problems"] = problems,
            ["changed"] = changed,
        };
        if (current && document.Renamed.Count > 0)
            answer["renamed"] = new JsonObject([.. document.Renamed.Select(r => KeyValuePair.Create(r.Key, (JsonNode?)r.Value))]);
        if (document.BaseName is { } name)
            answer["base"] = new JsonObject { ["pipeline"] = name, ["version"] = document.BaseVersion };
        return answer;
    }

    // The lines of the new text that differ from the old, counting from 1 (last may be first - 1 when lines were only
    // removed), and how many lines longer the new text is.
    private static (int First, int Last, int Shift) Changed(string was, string now)
    {
        var before = was.Split('\n');
        var after = now.Split('\n');
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)])
            suffix++;
        return (prefix + 1, after.Length - suffix, after.Length - before.Length);
    }

    // A graph with the spans in a file after its changed lines moved down (or up) by the lines added (or removed).
    private static PipelineGraph Shift(PipelineGraph graph, string path, int first, int lastBefore, int shift)
    {
        if (shift == 0)
            return graph;
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

    /// <summary>
    /// <c>meep/setBase</c>: matches an open document's modules with a running pipeline's from now on, so each has the
    /// key the daemon gives it, or will give it once the document is saved and reloaded.
    /// </summary>
    /// <remarks>
    /// Takes <c>{ textDocument, pipeline, graph, text }</c>: the pipeline's name, its graph from
    /// <c>GET /pipelines/{name}/graph</c>, and the text it runs from, from <c>GET /pipelines/{name}/source</c>. Send
    /// it again each time the daemon sends a <c>graph</c> event for the pipeline. <c>graph: null</c> stops matching
    /// with it. Answers as <c>meep/graph</c> does, with <c>renamed</c> for modules whose key changed; refused if the
    /// graph isn't of the text.
    /// </remarks>
    private async Task<JsonNode?> SetBaseAsync(JsonNode parameters, CancellationToken cancellationToken)
    {
        var uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
        if (!_documents.TryGetValue(uri, out var document))
            throw new LspException($"{uri} isn't open.");
        if (parameters["graph"] is not { } graphNode)
        {
            document.Draft.ClearBase();
            document.BaseName = null;
            document.BaseVersion = null;
            Unload(uri + BaseSuffix);
        }
        else
        {
            var graph = graphNode.Deserialize(DaemonJson.Default.PipelineGraph) ?? throw new LspException("graph isn't a pipeline's graph.");
            var text = parameters["text"]?.GetValue<string>() ?? throw new LspException("text is needed: the text the pipeline runs from, from GET /pipelines/{name}/source.");
            var syntax = Parse(document.Path, text);
            if (syntax.HasErrors)
                throw new LspException("The running pipeline's text doesn't parse here: " + string.Join("; ", syntax.Diagnostics.Where(d => d.IsError)));
            var samePlugins = document.Syntax is { } own && string.Join('\n', own.Plugins) == string.Join('\n', syntax.Plugins);
            var (registry, problems) = samePlugins && document.Registry is { } registered
                ? (registered, [])
                : await RegistryForAsync(uri + BaseSuffix, syntax, cancellationToken).ConfigureAwait(false);
            if (registry is null)
                throw new LspException("The running pipeline's plugins can't be loaded here: " + string.Join("; ", problems.Where(d => d.IsError)));
            var keys = graph.Modules.ToDictionary(m => m.Number);
            var (update, problem) = document.Draft.SetBase(syntax, registry, (number, verb) => keys.TryGetValue(number, out var module) && module.Verb == verb ? module.Key : null);
            if (update is null)
                throw new LspException(problem!);
            document.BaseName = parameters["pipeline"]?.GetValue<string>() ?? graph.Name;
            document.BaseVersion = graph.Version;
            if (update.Shape is { } shape)
                Record(document, shape, update.Renamed, document.GraphFrom ?? document.Version, document.GraphText ?? document.Text);
        }
        var answer = Answer(document);
        if (document.Subscribed)
            document.Sent = answer.ToJsonString();
        return answer;
    }

    /// <summary>
    /// <c>meep/verbs</c>: every verb and inflection a document can use, the core modules' and its plugins', each with
    /// its role, parameters (types, defaults and descriptions), the message types it takes and emits, and where its
    /// class is written. Without a document, the core modules'.
    /// </summary>
    private JsonNode Verbs(JsonNode? parameters) =>
        new JsonObject { ["verbs"] = JsonSerializer.SerializeToNode(VerbCatalogue.Describe(RegistryFor(parameters)), LspJson.Default.IReadOnlyListVerbEntry) };

    // textDocument/semanticTokens/full, and /full/delta when the client has the last result: Meep's parser's colours.
    private JsonNode? SemanticTokens(JsonNode parameters, string? previous)
    {
        if (!_documents.TryGetValue(parameters["textDocument"]!["uri"]!.GetValue<string>(), out var document))
            return null;
        var syntax = document.Syntax ?? Parse(document.Path, document.Text);
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
        var id = (++_tokenResults).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var last = document.Tokens;
        document.Tokens = (id, [.. data]);
        if (previous is not null && last is { } before && before.Id == previous)
        {
            var old = before.Data;
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
            return new JsonObject { ["resultId"] = id, ["edits"] = edits };
        }
        return new JsonObject { ["resultId"] = id, ["data"] = new JsonArray([.. data.Select(n => (JsonNode)n)]) };
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

    // What the server knows of an open file.
    private sealed class Document(string uri, string path)
    {
        public string Uri { get; } = uri;

        public string Path { get; } = path;

        public string Text { get; set; } = "";

        public int Version { get; set; }

        public PipelineSyntax? Syntax { get; set; }

        public ModuleRegistry? Registry { get; set; }

        public IReadOnlyList<Diagnostic> Problems { get; set; } = [];

        // Whether its text as it is now loaded.
        public bool Loaded { get; set; }

        public PipelineDraft Draft { get; } = new();

        public PipelineGraph? Graph { get; set; }

        public string? GraphJson { get; set; }

        public int GraphVersion { get; set; }

        public int? GraphFrom { get; set; }

        public string? GraphText { get; set; }

        public IReadOnlyDictionary<string, string> Renamed { get; set; } = new Dictionary<string, string>();

        public string? BaseName { get; set; }

        public int? BaseVersion { get; set; }

        public bool Subscribed { get; set; }

        public string? Sent { get; set; }

        public (string Id, int[] Data)? Tokens { get; set; }
    }

    // A request that can't be answered; its message goes back to the client.
    private sealed class LspException(string message) : Exception(message);
}

[JsonSerializable(typeof(IReadOnlyList<VerbEntry>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class LspJson : JsonSerializerContext;
