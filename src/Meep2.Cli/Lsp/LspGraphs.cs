using System.Text.Json;
using System.Text.Json.Nodes;
using Meep2.Daemon;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Syntax;

namespace Meep2.Cli;

/// <summary>
/// <c>meep/graph</c> and <c>meep/setBase</c>: open files' pipelines as graphs, as the daemon gives them, keyed to match a
/// running pipeline's, and <c>meep/graphChanged</c> for those subscribed to.
/// </summary>
/// <param name="workspace">The open files and the verbs each can use.</param>
/// <param name="rpc">Where notifications go.</param>
internal sealed class LspGraphs(LspWorkspace workspace, JsonRpcChannel rpc)
{
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
    public JsonNode? Graph(JsonNode parameters)
    {
        if (workspace.Of(parameters) is not { } document)
            return null;
        var answer = Answer(document);
        if (parameters["subscribe"]?.GetValue<bool>() is { } subscribe)
        {
            document.Subscribed = subscribe;
            document.Sent = answer.ToJsonString();
        }
        return answer;
    }

    /// <summary>Sends <c>meep/graphChanged</c> for a document that's subscribed to, if its answer changed.</summary>
    public Task ChangedAsync(LspDocument document, CancellationToken cancellationToken)
    {
        if (!document.Subscribed)
            return Task.CompletedTask;
        var answer = Answer(document);
        var json = answer.ToJsonString();
        if (json == document.Sent)
            return Task.CompletedTask;
        document.Sent = json;
        return rpc.NotifyAsync("meep/graphChanged", answer, cancellationToken);
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
    public async Task<JsonNode?> SetBaseAsync(JsonNode parameters, CancellationToken cancellationToken)
    {
        var uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
        if (!workspace.TryGet(uri, out var document))
            throw new LspException($"{uri} isn't open.");
        if (parameters["graph"] is not { } graphNode)
            ClearBase(document);
        else
            await SetBaseAsync(document, graphNode, parameters, cancellationToken).ConfigureAwait(false);
        var answer = Answer(document);
        if (document.Subscribed)
            document.Sent = answer.ToJsonString();
        return answer;
    }

    private void ClearBase(LspDocument document)
    {
        document.Draft.ClearBase();
        document.BaseName = null;
        document.BaseVersion = null;
        workspace.Unload(document.Uri + LspWorkspace.BaseSuffix);
    }

    private async Task SetBaseAsync(LspDocument document, JsonNode graphNode, JsonNode parameters, CancellationToken cancellationToken)
    {
        var graph = graphNode.Deserialize(DaemonJson.Default.PipelineGraph) ?? throw new LspException("graph isn't a pipeline's graph.");
        var text = parameters["text"]?.GetValue<string>() ?? throw new LspException("text is needed: the text the pipeline runs from, from GET /pipelines/{name}/source.");
        var syntax = workspace.Parse(document.Path, text);
        if (syntax.HasErrors)
            throw new LspException("The running pipeline's text doesn't parse here: " + string.Join("; ", syntax.Diagnostics.Where(d => d.IsError)));
        var registry = (await RegistryOfBaseAsync(document, syntax, cancellationToken).ConfigureAwait(false))
            .ValueOrThrow(problems => new LspException("The running pipeline's plugins can't be loaded here: " + problems));
        var keys = graph.Modules.ToDictionary(m => m.Number);
        var update = document.Draft.SetBase(syntax, registry, (number, verb) => keys.TryGetValue(number, out var module) && module.Verb == verb ? module.Key : null)
            .ValueOrThrow(problem => new LspException(problem));
        document.BaseName = parameters["pipeline"]?.GetValue<string>() ?? graph.Name;
        document.BaseVersion = graph.Version;
        if (update.Shape is { } shape)
            document.Record(shape, update.Renamed, document.GraphFrom ?? document.Version, document.GraphText ?? document.Text);
    }

    // The verbs the running pipeline's text can use: the document's, if they name the same plugins.
    private async Task<WithDiagnostics<ModuleRegistry>> RegistryOfBaseAsync(LspDocument document, PipelineSyntax syntax, CancellationToken cancellationToken)
    {
        var samePlugins = document.Syntax is { } own && string.Join('\n', own.Plugins) == string.Join('\n', syntax.Plugins);
        return samePlugins && document.Registry is { } registered
            ? new WithDiagnostics<ModuleRegistry>(registered, [])
            : await workspace.RegistryForAsync(document.Uri + LspWorkspace.BaseSuffix, syntax, cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject Answer(LspDocument document)
    {
        var current = document.Loaded && document.GraphFrom == document.Version;
        var graph = document.Graph;
        JsonObject? changed = null;
        if (graph is not null && !current && document.GraphText is { } was && was != document.Text)
        {
            var lines = ChangedLines.Between(was, document.Text);
            graph = lines.Move(graph, document.Path);
            changed = lines.ToRange();
        }
        var answer = new JsonObject
        {
            ["uri"] = document.Uri,
            ["version"] = document.Version,
            ["current"] = current,
            ["graph"] = graph is null ? null : JsonSerializer.SerializeToNode(graph, DaemonJson.Default.PipelineGraph),
            ["builtFrom"] = graph is null ? null : document.GraphFrom,
            ["problems"] = Problems(document),
            ["changed"] = changed,
        };
        if (current && document.Renamed.Count > 0)
            answer["renamed"] = new JsonObject([.. document.Renamed.Select(r => KeyValuePair.Create(r.Key, (JsonNode?)r.Value))]);
        if (document.BaseName is { } name)
            answer["base"] = new JsonObject { ["pipeline"] = name, ["version"] = document.BaseVersion };
        return answer;
    }

    // The errors that stop the text loading now, each in the file it's in.
    private static JsonArray Problems(LspDocument document)
    {
        var problems = new JsonArray();
        foreach (var problem in document.Problems)
        {
            problems.Add(new JsonObject
            {
                ["uri"] = problem.Span.File is { } file && file != document.Path ? new Uri(file).ToString() : document.Uri,
                ["range"] = LspRange.Of(problem.Span),
                ["message"] = problem.Message,
            });
        }
        return problems;
    }
}
