namespace Meep2.Plugins;

/// <summary>Keeps a plugin loaded. Dispose it when the pipeline that needed the plugin is finished with.</summary>
public sealed class PluginHold : IDisposable
{
    private readonly PluginCatalog _catalog;
    private LoadedPlugin? _plugin;

    internal PluginHold(PluginCatalog catalog, LoadedPlugin plugin)
    {
        _catalog = catalog;
        _plugin = plugin;
    }

    /// <summary>The plugin held.</summary>
    public LoadedPlugin Plugin => _plugin ?? throw new ObjectDisposedException(nameof(PluginHold));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _plugin, null) is { } plugin)
            _catalog.Release(plugin);
    }
}
