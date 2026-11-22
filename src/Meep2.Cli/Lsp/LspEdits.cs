using System.Text.Json;
using System.Text.Json.Nodes;
using Meep2.Daemon;
using Meep2.Meeplang.Editing;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Syntax;
using Meep2.Plugins;

namespace Meep2.Cli;

/// <summary><c>meep/applyEdits</c>: the changes to an open file's text that make edits to its modules.</summary>
/// <param name="workspace">The open files and the verbs each can use.</param>
internal sealed class LspEdits(LspWorkspace workspace)
{
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
    public async Task<JsonNode> ApplyEditsAsync(JsonNode parameters, CancellationToken cancellationToken)
    {
        var document = DocumentOf(parameters);
        var edits = EditsOf(parameters);
        var syntax = Loaded(document);
        var result = await MakeAsync(document, syntax, edits, cancellationToken).ConfigureAwait(false);
        var answer = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = document.Uri, ["version"] = document.Version },
            ["edits"] = TextEdits(document.Text, result.Syntax.Source.Text),
            ["added"] = new JsonArray([.. result.Added.Select(a => (JsonNode)new JsonObject { ["edit"] = a.Edit, ["key"] = a.Key })]),
        };
        if (parameters["preview"] is JsonValue previewValue && previewValue.TryGetValue<bool>(out var preview) && preview)
            AddPreview(answer, document, result);
        return answer;
    }

    // The open document the edits are for, at the version the server has.
    private LspDocument DocumentOf(JsonNode parameters)
    {
        var uri = parameters["textDocument"]?["uri"]?.GetValue<string>() ?? throw new LspException("textDocument.uri is needed: the document to edit.");
        if (!workspace.TryGet(uri, out var document))
            throw new LspException($"{uri} isn't open.");
        if (parameters["textDocument"]!["version"] is not JsonValue versionValue || !versionValue.TryGetValue<int>(out var version))
            throw new LspException("textDocument.version is needed: the version of the text the edits are for.");
        if (version != document.Version)
            throw new LspException($"The edits are for version {version} of the text, and the server has version {document.Version}. Nothing was changed. Send your changes to the text first, then ask again with its version.", LspException.ContentModified, new JsonObject { ["version"] = document.Version });
        return document;
    }

    // The edits asked for, as the daemon takes them.
    private static List<SourceEdit> EditsOf(JsonNode parameters)
    {
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
                throw new LspException($"{which} isn't one meep lsp can make: {exception.Message} Nothing was changed.", LspException.InvalidParams, new JsonObject { ["edit"] = i });
            }
        }
        return edits;
    }

    // The document's text as it loaded, which edits find its modules in.
    private static PipelineSyntax Loaded(LspDocument document)
    {
        if (document.Loaded && document.GraphFrom == document.Version && document.Syntax is { } syntax)
            return syntax;
        var problem = document.Problems.FirstOrDefault(p => p.Span.File is null) ?? document.Problems.FirstOrDefault();
        var why = problem is null ? "" : $" (line {problem.Span.Line}: {problem.Message})";
        throw new LspException($"The text doesn't load as it is{why}, so its modules can't be found. Nothing was changed. Edits can be made once it loads.", LspException.RequestFailed, new JsonObject { ["edit"] = null });
    }

    // Makes the edits. Each text they make is loaded with the document's verbs while its #plugin lines are the same.
    private async Task<DraftEdits> MakeAsync(LspDocument document, PipelineSyntax syntax, List<SourceEdit> edits, CancellationToken cancellationToken)
    {
        var scratch = document.Uri + LspWorkspace.EditsSuffix;
        var plugins = PluginRegistries.LinesOf(syntax);
        async Task<WithDiagnostics<ModuleRegistry>> RegistryOf(PipelineSyntax text, CancellationToken token) =>
            PluginRegistries.LinesOf(text) == plugins && document.Registry is { } registry
                ? new(registry, [])
                : await workspace.RegistryForAsync(scratch, text, token).ConfigureAwait(false);
        try
        {
            return await DraftEditor.ApplyAsync(document.Draft, syntax, edits, text => workspace.Parse(document.Path, text), RegistryOf, cancellationToken).ConfigureAwait(false);
        }
        catch (DraftEditException exception)
        {
            throw new LspException(exception.Message, LspException.RequestFailed, new JsonObject { ["edit"] = exception.Edit < 0 ? null : exception.Edit });
        }
        finally
        {
            workspace.Unload(scratch);
        }
    }

    // The changes from one text to another, as LSP's TextEdits.
    private static JsonArray TextEdits(string was, string now)
    {
        var changes = new JsonArray();
        foreach (var change in TextChanges.Between(was, now))
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
        return changes;
    }

    // The graph the text will have, with the version it will have: the same if nothing in it changes, as Record counts.
    private static void AddPreview(JsonObject answer, LspDocument document, DraftEdits result)
    {
        var graph = document.GraphOf(result.Update.Shape!);
        var unchanged = JsonSerializer.Serialize(graph, DaemonJson.Default.PipelineGraph) == document.GraphJson;
        answer["graph"] = JsonSerializer.SerializeToNode(graph with { Version = unchanged ? document.GraphVersion : document.GraphVersion + 1 }, DaemonJson.Default.PipelineGraph);
        if (result.Update.Renamed.Count > 0)
            answer["renamed"] = new JsonObject([.. result.Update.Renamed.Select(r => KeyValuePair.Create(r.Key, (JsonNode?)r.Value))]);
    }
}
