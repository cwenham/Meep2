using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Meep2.Meeplang.Editing;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Syntax;
using Meep2.Plugins;

namespace Meep2.Cli;

/// <summary>
/// <c>meep lsp</c>: a Language Server Protocol server for Meeplang, over standard input and output, for the VSCode
/// extension and any other editor that speaks LSP (step 9). It checks a file as it's edited with Meeplang's own parser
/// and loader, so an editor shows the same mistakes <c>meep run</c> would, and offers completions, hovers and semantic
/// tokens. For tools that draw pipelines, it adds <c>meep/graph</c>, <c>meep/setBase</c>, <c>meep/verbs</c> and
/// <c>meep/applyEdits</c>: see <see cref="LspGraphs"/>, <see cref="Verbs"/> and
/// <see cref="LspEdits"/>.
/// </summary>
/// <remarks>
/// A file is loaded against the core modules and the plugins its <c>#plugin</c> lines name, so wrong verbs, parameters
/// and expressions show too. Plugins come only from Meep's cache, with nothing downloaded, and only those
/// <c>meep run</c> would trust without <c>--dev</c>, so opening a file can't load code you haven't trusted (Q138). A
/// plugin that isn't there is reported on its line, and the rest of the file is only parsed.
/// </remarks>
internal sealed class LanguageServer
{
    private readonly JsonRpcChannel _rpc;
    private readonly LspWorkspace _workspace;
    private readonly LspCompletion _completion;
    private readonly LspHover _hover;
    private readonly LspGraphs _graphs;
    private readonly LspEdits _edits;
    private readonly LspTokens _tokens;
    private readonly LspPermissions _permissions;
    private bool _shuttingDown;

    /// <summary>Makes a server that reads from <paramref name="input"/> and writes to <paramref name="output"/>.</summary>
    /// <param name="input">What the editor sends.</param>
    /// <param name="output">Where replies go.</param>
    /// <param name="plugins">Where plugins are cached, and the keys you trust; without it, a file with plugins is only parsed.</param>
    public LanguageServer(Stream input, Stream output, PluginOptions? plugins = null)
    {
        _rpc = new JsonRpcChannel(input, output);
        _workspace = new LspWorkspace(plugins);
        _completion = new LspCompletion(_workspace);
        _hover = new LspHover(_workspace);
        _graphs = new LspGraphs(_workspace, _rpc);
        _edits = new LspEdits(_workspace);
        _tokens = new LspTokens(_workspace);
        _permissions = new LspPermissions(plugins?.Home);
    }

    /// <summary>Serves requests until the client says to exit, or closes the stream. Returns the exit code.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        while (await _rpc.ReadAsync(cancellationToken).ConfigureAwait(false) is { } message)
        {
            if (message is not JsonObject)
            {
                await _rpc.RespondErrorAsync(null, -32600, "meep lsp takes JSON-RPC messages, which are JSON objects.", cancellationToken).ConfigureAwait(false);
                continue;
            }
            JsonNode? id = null;
            string? method = null;
            try
            {
                id = message["id"];
                method = message["method"] is JsonValue name && name.TryGetValue<string>(out var text) ? text : null;
                if (method == "exit")
                {
                    _workspace.Dispose();
                    return _shuttingDown ? 0 : 1;
                }
                await HandleAsync(id, method, message["params"], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not IOException and not OutOfMemoryException)
            {
                // A message missing what it needs, or with the wrong kind of value, is answered as invalid, and the
                // server carries on with the next (S10). A notification has nobody to answer.
                if (id is not null)
                    await _rpc.RespondErrorAsync(id, -32602, $"meep lsp couldn't handle {method ?? "a message without a method"}: {exception.Message}", cancellationToken).ConfigureAwait(false);
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
        var document = _workspace.Open(uri, path);
        document.Text = text;
        return await AnalyseAsync(document, cancellationToken).ConfigureAwait(false);
    }

    // Each method the server handles, and what answers it. Requests it doesn't handle are refused; notifications it
    // doesn't handle are let pass.
    private Task HandleAsync(JsonNode? id, string? method, JsonNode? parameters, CancellationToken cancellationToken) => method switch
    {
        "initialize" => _rpc.RespondAsync(id, Capabilities(), cancellationToken),
        "shutdown" => ShutdownAsync(id, cancellationToken),
        "textDocument/didOpen" => ChangedAsync(
            parameters!["textDocument"]!["uri"]!.GetValue<string>(),
            parameters["textDocument"]!["text"]!.GetValue<string>(),
            parameters["textDocument"]!["version"]?.GetValue<int>() ?? 0,
            cancellationToken),
        "textDocument/didChange" => DidChangeAsync(parameters!, cancellationToken),
        "textDocument/didClose" => ClosedAsync(parameters!["textDocument"]!["uri"]!.GetValue<string>(), cancellationToken),
        "textDocument/semanticTokens/full" => _rpc.RespondAsync(id, _tokens.Tokens(parameters!, null), cancellationToken),
        "textDocument/semanticTokens/full/delta" => _rpc.RespondAsync(id, _tokens.Tokens(parameters!, parameters!["previousResultId"]?.GetValue<string>()), cancellationToken),
        "meep/graph" => _rpc.RespondAsync(id, _graphs.Graph(parameters!), cancellationToken),
        "meep/setBase" => AnswerOrRefuseAsync(id, () => _graphs.SetBaseAsync(parameters!, cancellationToken), cancellationToken),
        "meep/applyEdits" => AnswerOrRefuseAsync(id, async () => await _edits.ApplyEditsAsync(parameters!, cancellationToken).ConfigureAwait(false), cancellationToken),
        "meep/verbs" => _rpc.RespondAsync(id, Verbs(parameters), cancellationToken),
        "textDocument/completion" => _rpc.RespondAsync(id, _completion.Complete(parameters!), cancellationToken),
        "textDocument/hover" => _rpc.RespondAsync(id, _hover.Hover(parameters!), cancellationToken),
        _ => id is null ? Task.CompletedTask : _rpc.RespondErrorAsync(id, -32601, $"meep lsp doesn't handle {method}.", cancellationToken),
    };

    // What the server can do, for initialize.
    private static JsonObject Capabilities() => new()
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
    };

    private async Task ShutdownAsync(JsonNode? id, CancellationToken cancellationToken)
    {
        _shuttingDown = true;
        await _rpc.RespondAsync(id, null, cancellationToken).ConfigureAwait(false);
    }

    // Answers with what a request makes, or refuses it with the code and details its LspException gives.
    private async Task AnswerOrRefuseAsync(JsonNode? id, Func<Task<JsonNode?>> answer, CancellationToken cancellationToken)
    {
        try
        {
            await _rpc.RespondAsync(id, await answer().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        catch (LspException exception)
        {
            await _rpc.RespondErrorAsync(id!, exception.Code, exception.Message, cancellationToken, exception.Details).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>meep/verbs</c>: every verb and inflection a document can use, the core modules' and its plugins', each with
    /// its role, parameters (types, defaults and descriptions), the message types it takes and emits, and where its
    /// class is written. Without a document, the core modules'.
    /// </summary>
    private JsonNode Verbs(JsonNode? parameters) =>
        new JsonObject { ["verbs"] = JsonSerializer.SerializeToNode(VerbCatalogue.Describe(_workspace.RegistryFor(parameters)), LspJson.Default.IReadOnlyListVerbEntry) };

    // Parses a document, loads it with the verbs it can use, and works out its graph if it loads.
    private async Task<IReadOnlyList<Diagnostic>> AnalyseAsync(LspDocument document, CancellationToken cancellationToken)
    {
        var syntax = _workspace.Parse(document.Path, document.Text);
        document.Syntax = syntax;
        if (syntax.HasErrors)
        {
            document.Loaded = false;
            return syntax.Diagnostics;
        }
        var (registry, problems) = await _workspace.RegistryForAsync(document.Uri, syntax, cancellationToken).ConfigureAwait(false);
        document.Registry = registry ?? document.Registry;
        if (registry is null)
        {
            document.Loaded = false;
            return problems;
        }
        var update = document.Draft.Update(syntax, registry);
        document.Loaded = update.Loaded;
        if (!update.Loaded)
            return [.. problems, .. update.Diagnostics];
        document.Record(update.Shape!, update.Renamed, document.Version, document.Text);
        return [.. problems, .. update.Diagnostics, .. _permissions.Warnings(update.Shape!, document.Path)];
    }

    private async Task DidChangeAsync(JsonNode parameters, CancellationToken cancellationToken)
    {
        var changes = parameters["contentChanges"]!.AsArray();
        if (changes.Count > 0)
        {
            await ChangedAsync(
                parameters["textDocument"]!["uri"]!.GetValue<string>(),
                changes[^1]!["text"]!.GetValue<string>(),
                parameters["textDocument"]!["version"]?.GetValue<int>() ?? 0,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ChangedAsync(string uri, string text, int version, CancellationToken cancellationToken)
    {
        var document = _workspace.Open(uri, PathOf(uri));
        document.Text = text;
        document.Version = version;
        await CheckAndPublishAsync(document, cancellationToken).ConfigureAwait(false);

        // Files that include this one change with it: a template edited here changes the pipelines that use it.
        foreach (var including in _workspace.Documents.Where(d => d != document && d.Syntax?.Included.Any(i => i.Source.Path == document.Path) == true).ToList())
            await CheckAndPublishAsync(including, cancellationToken).ConfigureAwait(false);
    }

    private async Task ClosedAsync(string uri, CancellationToken cancellationToken)
    {
        _workspace.Close(uri);
        await PublishAsync(uri, [], cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckAndPublishAsync(LspDocument document, CancellationToken cancellationToken)
    {
        var path = document.Path;
        var diagnostics = await AnalyseAsync(document, cancellationToken).ConfigureAwait(false);
        document.Problems = [.. diagnostics.Where(d => d.IsError)];
        await PublishAsync(document.Uri, [.. diagnostics.Where(d => d.Span.File is null || d.Span.File == path)], cancellationToken).ConfigureAwait(false);
        foreach (var included in diagnostics.Where(d => d.Span.File is { } file && file != path).GroupBy(d => d.Span.File!))
            await PublishAsync(new Uri(included.Key).ToString(), [.. included], cancellationToken).ConfigureAwait(false);
        await _graphs.ChangedAsync(document, cancellationToken).ConfigureAwait(false);
    }

    private Task PublishAsync(string uri, IReadOnlyList<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var list = new JsonArray();
        foreach (var diagnostic in diagnostics)
        {
            list.Add(new JsonObject
            {
                ["range"] = LspRange.Of(diagnostic.Span),
                ["severity"] = diagnostic.IsError ? 1 : 2,
                ["source"] = "meep",
                ["message"] = diagnostic.Message,
            });
        }
        return _rpc.NotifyAsync("textDocument/publishDiagnostics", new JsonObject { ["uri"] = uri, ["diagnostics"] = list }, cancellationToken);
    }

    private static string PathOf(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : uri;
}

[JsonSerializable(typeof(IReadOnlyList<VerbEntry>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class LspJson : JsonSerializerContext;
