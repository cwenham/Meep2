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
/// tokens. For tools that draw pipelines, it adds <c>meep/graph</c>, <c>meep/setBase</c>, <c>meep/verbs</c> and
/// <c>meep/applyEdits</c>: see <see cref="Graph"/>, <see cref="SetBaseAsync"/>, <see cref="Verbs"/> and
/// <see cref="ApplyEditsAsync"/>.
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

    // LSP's error codes.
    private const int InvalidParams = -32602;
    private const int ContentModified = -32801;
    private const int RequestFailed = -32803;

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
            if (message is not JsonObject)
            {
                await RespondErrorAsync(null, -32600, "meep lsp takes JSON-RPC messages, which are JSON objects.", cancellationToken).ConfigureAwait(false);
                continue;
            }
            JsonNode? id = null;
            string? method = null;
            try
            {
                id = message["id"];
                method = message["method"] is JsonValue name && name.TryGetValue<string>(out var text) ? text : null;
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
                                    ["meep"] = new JsonObject { ["graph"] = true, ["setBase"] = true, ["verbs"] = true, ["applyEdits"] = true },
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
                            await RespondErrorAsync(id!, exception.Code, exception.Message, cancellationToken, exception.Details).ConfigureAwait(false);
                        }
                        break;
                    case "meep/applyEdits":
                        try
                        {
                            await RespondAsync(id, await ApplyEditsAsync(parameters!, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                        }
                        catch (LspException exception)
                        {
                            await RespondErrorAsync(id!, exception.Code, exception.Message, cancellationToken, exception.Details).ConfigureAwait(false);
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
            catch (Exception exception) when (exception is not OperationCanceledException and not IOException and not OutOfMemoryException)
            {
                // A message missing what it needs, or with the wrong kind of value, is answered as invalid, and the
                // server carries on with the next (S10). A notification has nobody to answer.
                if (id is not null)
                    await RespondErrorAsync(id, -32602, $"meep lsp couldn't handle {method ?? "a message without a method"}: {exception.Message}", cancellationToken).ConfigureAwait(false);
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
    private async Task<WithDiagnostics<ModuleRegistry>> RegistryForAsync(string key, PipelineSyntax syntax, CancellationToken cancellationToken)
    {
        var registry = _registry.Clone();
        registry.Add<WriteLine, WriteLineParameters>("WriteLine", parameters => new WriteLine(parameters, TextWriter.Null));
        if (syntax.Plugins.Count == 0)
        {
            Unload(key);
            return new(registry, []);
        }
        if (_installer is null)
            return new(null, syntax.Diagnostics);
        var lines = string.Join("\n", syntax.Plugins.Select(p => $"{p} as {p.Alias}"));
        if (!_loaded.TryGetValue(key, out var loaded) || loaded.Lines != lines)
        {
            Unload(key);
            loaded = (lines, await Install.InstallAllAsync(syntax, registry, _installer, _catalog, saveLock: false, cancellationToken).ConfigureAwait(false));
            _loaded[key] = loaded;
        }
        if (loaded.Installed.Problems.Count > 0)
            return new(null, [.. syntax.Diagnostics, .. loaded.Installed.Warnings, .. loaded.Installed.Problems]);
        return new(loaded.Installed.Registry, loaded.Installed.Warnings);
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
        var graph = GraphOf(document, shape);
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

    // A shape as the daemon's graph, named as the document's graph is, before it's given a version.
    private static PipelineGraph GraphOf(Document document, PipelineShape shape) =>
        Graphs.Describe(document.BaseName ?? System.IO.Path.GetFileNameWithoutExtension(document.Path), document.Path, shape, version: 0);

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
            var registry = (samePlugins && document.Registry is { } registered
                    ? new WithDiagnostics<ModuleRegistry>(registered, [])
                    : await RegistryForAsync(uri + BaseSuffix, syntax, cancellationToken).ConfigureAwait(false))
                .ValueOrThrow(problems => new LspException("The running pipeline's plugins can't be loaded here: " + problems));
            var keys = graph.Modules.ToDictionary(m => m.Number);
            var update = document.Draft.SetBase(syntax, registry, (number, verb) => keys.TryGetValue(number, out var module) && module.Verb == verb ? module.Key : null)
                .ValueOrThrow(problem => new LspException(problem));
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

    // The key a document's edits keep the plugins of their texts loaded under, while they're worked out.
    private const string EditsSuffix = "#edits";

    /// <summary>
    /// <c>meep/applyEdits</c>: makes edits to an open document's text, as <c>POST /pipelines/{name}/edits</c> takes
    /// them, with <see cref="SourceEditor"/>, and answers with the changes to the text that make them, for the client
    /// to apply. It changes nothing itself: the client applies the changes, as one step it can undo, and sends
    /// <c>didChange</c> as usual.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes <c>{ textDocument: { uri, version }, edits: [ { module, kind, name, value } ], preview }</c>. The version
    /// must be the server's latest, and the text must load as it is. A module is named by its key in
    /// <c>meep/graph</c> (<c>m3</c>), its name, or its number; see <see cref="DraftEditor"/>. The edits are made in
    /// order, as one: if one can't be made, or the text they leave doesn't load, none are, and the error says which
    /// edit and why (<c>data.edit</c> counts from 0).
    /// </para>
    /// <para>
    /// The answer is an LSP <c>TextDocumentEdit</c>, <c>{ textDocument, edits }</c>, whose <c>TextEdit</c>s change
    /// that version of the text as little as they can, with <c>added</c>: for each module an <c>add</c> or
    /// <c>insert</c> writes, <c>{ edit, key }</c>, the key the graph gives it once the client sends the new text. With
    /// <c>preview: true</c>, it also has <c>graph</c>, the graph the text will have then, as <c>meep/graph</c> will
    /// give it, and <c>renamed</c> as <c>meep/graph</c> has it.
    /// </para>
    /// </remarks>
    private async Task<JsonNode> ApplyEditsAsync(JsonNode parameters, CancellationToken cancellationToken)
    {
        var uri = parameters["textDocument"]?["uri"]?.GetValue<string>() ?? throw new LspException("textDocument.uri is needed: the document to edit.");
        if (!_documents.TryGetValue(uri, out var document))
            throw new LspException($"{uri} isn't open.");
        if (parameters["textDocument"]!["version"] is not JsonValue versionValue || !versionValue.TryGetValue<int>(out var version))
            throw new LspException("textDocument.version is needed: the version of the text the edits are for.");
        if (version != document.Version)
            throw new LspException($"The edits are for version {version} of the text, and the server has version {document.Version}. Nothing was changed. Send your changes to the text first, then ask again with its version.", ContentModified, new JsonObject { ["version"] = document.Version });
        if (parameters["edits"] is not JsonArray list || list.Count == 0)
            throw new LspException("edits is needed: a list of edits, such as [{\"module\": \"m3\", \"kind\": \"set\", \"name\": \"timeout\", \"value\": \"5s\"}].");
        var edits = new List<SourceEdit>();
        for (var i = 0; i < list.Count; i++)
        {
            try
            {
                var item = list[i]?.Deserialize(DaemonJson.Default.EditItem) ?? throw new JsonException("It's empty.");
                edits.Add(Reloading.EditOf(item));
            }
            catch (Exception exception) when (exception is JsonException or DaemonException or InvalidOperationException)
            {
                var which = list.Count == 1 ? "The edit" : $"Edit {i + 1} of {list.Count}";
                throw new LspException($"{which} isn't one meep lsp can make: {exception.Message} Nothing was changed.", InvalidParams, new JsonObject { ["edit"] = i });
            }
        }
        if (!document.Loaded || document.GraphFrom != document.Version || document.Syntax is not { } syntax)
        {
            var problem = document.Problems.FirstOrDefault(p => p.Span.File is null) ?? document.Problems.FirstOrDefault();
            var why = problem is null ? "" : $" (line {problem.Span.Line}: {problem.Message})";
            throw new LspException($"The text doesn't load as it is{why}, so its modules can't be found. Nothing was changed. Edits can be made once it loads.", RequestFailed, new JsonObject { ["edit"] = null });
        }

        // Each text the edits make is loaded with the document's verbs while its #plugin lines are the same.
        var scratch = uri + EditsSuffix;
        var plugins = Plugins(syntax);
        async Task<WithDiagnostics<ModuleRegistry>> RegistryOf(PipelineSyntax text, CancellationToken token) =>
            Plugins(text) == plugins && document.Registry is { } registry
                ? new(registry, [])
                : await RegistryForAsync(scratch, text, token).ConfigureAwait(false);
        DraftEdits result;
        try
        {
            result = await DraftEditor.ApplyAsync(document.Draft, syntax, edits, text => Parse(document.Path, text), RegistryOf, cancellationToken).ConfigureAwait(false);
        }
        catch (DraftEditException exception)
        {
            throw new LspException(exception.Message, RequestFailed, new JsonObject { ["edit"] = exception.Edit < 0 ? null : exception.Edit });
        }
        finally
        {
            Unload(scratch);
        }

        var changes = new JsonArray();
        foreach (var change in TextChanges.Between(document.Text, result.Syntax.Source.Text))
        {
            changes.Add(new JsonObject
            {
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = change.StartLine, ["character"] = change.StartCharacter },
                    ["end"] = new JsonObject { ["line"] = change.EndLine, ["character"] = change.EndCharacter },
                },
                ["newText"] = change.NewText,
            });
        }
        var answer = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
            ["edits"] = changes,
            ["added"] = new JsonArray([.. result.Added.Select(a => (JsonNode)new JsonObject { ["edit"] = a.Edit, ["key"] = a.Key })]),
        };
        if (parameters["preview"] is JsonValue previewValue && previewValue.TryGetValue<bool>(out var preview) && preview)
        {
            // The version the graph will have: the same if nothing in it changes, as Record counts.
            var graph = GraphOf(document, result.Update.Shape!);
            var unchanged = JsonSerializer.Serialize(graph, DaemonJson.Default.PipelineGraph) == document.GraphJson;
            answer["graph"] = JsonSerializer.SerializeToNode(graph with { Version = unchanged ? document.GraphVersion : document.GraphVersion + 1 }, DaemonJson.Default.PipelineGraph);
            if (result.Update.Renamed.Count > 0)
                answer["renamed"] = new JsonObject([.. result.Update.Renamed.Select(r => KeyValuePair.Create(r.Key, (JsonNode?)r.Value))]);
        }
        return answer;

        static string Plugins(PipelineSyntax text) => string.Join("\n", text.Plugins.Select(p => $"{p} as {p.Alias}"));
    }

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

    // An error answer; with a null id, for a message whose id couldn't be read, as JSON-RPC says.
    private Task RespondErrorAsync(JsonNode? id, int code, string message, CancellationToken cancellationToken, JsonNode? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null)
            error["data"] = data.DeepClone();
        return WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = error }, cancellationToken);
    }

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

    /// <summary>The largest message meep lsp reads: 32 MiB, far more than any file's text.</summary>
    internal const int MaxMessage = 32 * 1024 * 1024;

    // The next message, or null once the stream ends or its framing can't be followed. A message too large, or not
    // JSON, is answered with an error and skipped (S10).
    private async Task<JsonNode?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            long length = -1;
            var malformed = false;
            while (true)
            {
                var line = await ReadHeaderLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    return null;
                if (line.Length == 0)
                    break;
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    malformed = !long.TryParse(line["Content-Length:".Length..].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out length);
                }
            }
            if (malformed || length < 0)
            {
                // Without a length, where the next message starts can't be known, so the server stops, saying why.
                await RespondErrorAsync(null, -32700, "meep lsp read a message without a Content-Length it could follow, so it has stopped.", cancellationToken).ConfigureAwait(false);
                return null;
            }
            if (length > MaxMessage)
            {
                await SkipAsync(length, cancellationToken).ConfigureAwait(false);
                await RespondErrorAsync(null, -32600, $"meep lsp skipped a message of {length} bytes: messages can be at most {MaxMessage}.", cancellationToken).ConfigureAwait(false);
                continue;
            }
            var body = new byte[length];
            await input.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
            try
            {
                return JsonNode.Parse(body) ?? new JsonObject();
            }
            catch (JsonException exception)
            {
                await RespondErrorAsync(null, -32700, $"meep lsp couldn't read a message as JSON: {exception.Message}", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Reads past a body without keeping it.
    private async Task SkipAsync(long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        while (length > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return;
            length -= read;
        }
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

    // A request that can't be answered; its message goes back to the client, with LSP's code for why, and details.
    private sealed class LspException(string message, int code = InvalidParams, JsonNode? details = null) : Exception(message)
    {
        public int Code { get; } = code;

        public JsonNode? Details { get; } = details;
    }
}

[JsonSerializable(typeof(IReadOnlyList<VerbEntry>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class LspJson : JsonSerializerContext;
