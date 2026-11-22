namespace Meep2.Plugins;

/// <summary>
/// The plugins loaded in this process. A plugin loads into a load context of its own the first time a pipeline needs
/// it, is shared by every pipeline that uses it, and unloads once the last one lets go (Q35).
/// </summary>
/// <remarks>
/// Unloading is cooperative in .NET: the context goes once nothing refers to its types. A message of a plugin's type
/// that something still holds, such as a cache, keeps the plugin loaded until it's gone.
/// </remarks>
public sealed class PluginCatalog
{
    private readonly Dictionary<string, LoadedPlugin> _loaded = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>The catalog for this process.</summary>
    public static PluginCatalog Shared { get; } = new();

    /// <summary>The plugins loaded now.</summary>
    public IReadOnlyList<LoadedPlugin> Loaded
    {
        get
        {
            lock (_lock)
                return [.. _loaded.Values];
        }
    }

    /// <summary>Loads an installed plugin, or finds it loaded already, and holds it loaded until the hold is disposed.</summary>
    public PluginHold Acquire(InstalledPlugin installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        lock (_lock)
        {
            if (!_loaded.TryGetValue(installed.Key, out var plugin))
            {
                plugin = LoadedPlugin.Load(installed);
                _loaded[installed.Key] = plugin;
            }
            plugin.Holds++;
            return new PluginHold(this, plugin);
        }
    }

    internal void Release(LoadedPlugin plugin)
    {
        lock (_lock)
        {
            if (--plugin.Holds > 0)
                return;
            _loaded.Remove(plugin.Installed.Key);
        }
        plugin.Unload();
    }
}
