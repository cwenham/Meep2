using Meep2.Meeplang.Editing;
using Meep2.Meeplang.Hosting;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Syntax;

namespace Meep2.Plugins;

/// <summary>
/// The verbs pipeline texts can use while they're being edited: a base registry with the plugins each text's
/// <c>#plugin</c> lines name, installed and kept loaded under a key while the lines stay the same, as <c>meep lsp</c>
/// and the daemon's edits by key need them. Disposing it lets go of every plugin it loaded.
/// </summary>
/// <param name="installer">Installs and checks plugins.</param>
/// <param name="catalog">Loads plugins and shares them between pipelines.</param>
public sealed class PluginRegistries(PluginInstaller installer, PluginCatalog catalog) : IDisposable
{
    private readonly Dictionary<string, (string Lines, InstalledPlugins Installed)> _loaded = new(StringComparer.Ordinal);

    /// <summary>A text's <c>#plugin</c> lines, one to a line, with their names, to tell whether two texts have the same plugins.</summary>
    public static string LinesOf(PipelineSyntax syntax) => string.Join("\n", syntax.Plugins.Select(p => $"{p} as {p.Alias}"));

    /// <summary>
    /// The verbs <paramref name="syntax"/> can use: <paramref name="registry"/>, with the plugins it names loaded under
    /// <paramref name="key"/> until its lines change or the key is unloaded. Null, with the problems, if a plugin can't
    /// be loaded. Warnings come back either way.
    /// </summary>
    public async Task<WithDiagnostics<ModuleRegistry>> RegistryForAsync(string key, PipelineSyntax syntax, ModuleRegistry registry, CancellationToken cancellationToken)
    {
        if (syntax.Plugins.Count == 0)
        {
            Unload(key);
            return new(registry, []);
        }
        var lines = LinesOf(syntax);
        if (!_loaded.TryGetValue(key, out var loaded) || loaded.Lines != lines)
        {
            Unload(key);
            loaded = (lines, await Install.InstallAllAsync(syntax, registry, installer, catalog, saveLock: false, cancellationToken).ConfigureAwait(false));
            _loaded[key] = loaded;
        }
        return loaded.Installed.Problems.Count > 0
            ? new(null, [.. loaded.Installed.Warnings, .. loaded.Installed.Problems])
            : new(loaded.Installed.Registry, loaded.Installed.Warnings);
    }

    /// <summary>
    /// Finds the verbs for texts edited from <paramref name="reference"/>: its <paramref name="registry"/> while their
    /// <c>#plugin</c> lines are the same as its, and otherwise <paramref name="baseRegistry"/> with their own plugins,
    /// loaded under <paramref name="key"/>, or with no key, each set of lines kept loaded under its own.
    /// </summary>
    public RegistryLookup Reusing(PipelineSyntax reference, ModuleRegistry registry, ModuleRegistry baseRegistry, string? key = null)
    {
        var lines = LinesOf(reference);
        return async (text, cancellationToken) =>
            LinesOf(text) == lines
                ? new(registry, [])
                : await RegistryForAsync(key ?? LinesOf(text), text, baseRegistry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The verbs loaded under <paramref name="key"/>, if its plugins all loaded.</summary>
    public ModuleRegistry? Loaded(string key) =>
        _loaded.TryGetValue(key, out var loaded) && loaded.Installed.Problems.Count == 0 ? loaded.Installed.Registry : null;

    /// <summary>Lets go of the plugins loaded under <paramref name="key"/>.</summary>
    public void Unload(string key)
    {
        if (_loaded.Remove(key, out var loaded))
            Build.Release(loaded.Installed.Holds);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var key in _loaded.Keys.ToList())
            Unload(key);
    }
}
