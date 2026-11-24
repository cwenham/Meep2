using System.Text.Json.Nodes;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.Plugins;

namespace Meep2.Cli;

/// <summary>
/// The files open in <c>meep lsp</c>, and the verbs each can use: the core modules', and the plugins its
/// <c>#plugin</c> lines name, loaded from the cache and kept loaded while the lines stay the same.
/// </summary>
/// <param name="plugins">Where plugins are cached, and the keys you trust; without it, a file with plugins is only parsed.</param>
internal sealed class LspWorkspace(PluginOptions? plugins) : IDisposable
{
    /// <summary>The key a document's base keeps its plugins loaded under, beside the document's own.</summary>
    public const string BaseSuffix = "#base";

    /// <summary>The key a document's edits keep the plugins of their texts loaded under, while they're worked out.</summary>
    public const string EditsSuffix = "#edits";

    private readonly Dictionary<string, LspDocument> _documents = new(StringComparer.Ordinal);
    private readonly ModuleRegistry _registry = new ModuleRegistry().AddAssembly(CoreModules.Assembly).AddAssembly(RuntimeModules.Assembly);

    // Each open file's plugins, kept loaded while its #plugin lines stay the same.
    private readonly PluginRegistries? _plugins = plugins is null ? null : new PluginRegistries(new PluginInstaller(plugins with { Offline = true, DeveloperMode = false }), new PluginCatalog());

    /// <summary>The open files.</summary>
    public IEnumerable<LspDocument> Documents => _documents.Values;

    /// <summary>The open file an editor names by <paramref name="uri"/>.</summary>
    public bool TryGet(string uri, out LspDocument document) => _documents.TryGetValue(uri, out document!);

    /// <summary>The open file a request's <c>textDocument.uri</c> names, if it's open.</summary>
    public LspDocument? Of(JsonNode parameters) =>
        _documents.TryGetValue(parameters["textDocument"]!["uri"]!.GetValue<string>(), out var document) ? document : null;

    /// <summary>The file <paramref name="uri"/> names, opened with <paramref name="path"/> if it isn't open yet.</summary>
    public LspDocument Open(string uri, string path)
    {
        if (!_documents.TryGetValue(uri, out var document))
            _documents[uri] = document = new LspDocument(uri, path);
        return document;
    }

    /// <summary>Forgets a file, and lets go of its plugins and its base's.</summary>
    public void Close(string uri)
    {
        _documents.Remove(uri);
        Unload(uri);
        Unload(uri + BaseSuffix);
    }

    /// <summary>A file parsed, with the files it includes as they are in the editor, if they're open, or else on disk.</summary>
    public PipelineSyntax Parse(string path, string text) =>
        MeeplangParser.Parse(new SourceFile(path, text), included =>
            _documents.Values.FirstOrDefault(d => d.Path == included) is { } open ? open.Text
            : File.Exists(included) ? File.ReadAllText(included)
            : null);

    /// <summary>
    /// The verbs a parsed file can use: the core modules', and the plugins its #plugin lines name, loaded from the
    /// cache and kept loaded under <paramref name="key"/> while the lines stay the same. Null, with the problems, if a
    /// plugin can't be loaded; then the file is only parsed. Warnings come back either way.
    /// </summary>
    public async Task<WithDiagnostics<ModuleRegistry>> RegistryForAsync(string key, PipelineSyntax syntax, CancellationToken cancellationToken)
    {
        var registry = _registry.Clone();
        registry.Add<WriteLine, WriteLineParameters>("WriteLine", parameters => new WriteLine(parameters, TextWriter.Null));
        if (syntax.Plugins.Count == 0)
        {
            Unload(key);
            return new(registry, []);
        }
        if (_plugins is null)
            return new(null, syntax.Diagnostics);
        var loaded = await _plugins.RegistryForAsync(key, syntax, registry, cancellationToken).ConfigureAwait(false);
        return loaded.Value is null ? loaded with { Diagnostics = [.. syntax.Diagnostics, .. loaded.Diagnostics] } : loaded;
    }

    /// <summary>Lets go of the plugins kept under <paramref name="key"/>.</summary>
    public void Unload(string key) => _plugins?.Unload(key);

    /// <summary>The verbs the file a request names can use: the core modules', and its plugins' once they've loaded.</summary>
    public ModuleRegistry RegistryFor(JsonNode? parameters) =>
        parameters?["textDocument"]?["uri"]?.GetValue<string>() is { } uri && _plugins?.Loaded(uri) is { } loaded
            ? loaded
            : _registry;

    /// <inheritdoc/>
    public void Dispose() => _plugins?.Dispose();
}
