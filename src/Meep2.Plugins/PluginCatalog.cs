using System.Reflection;
using System.Runtime.Loader;

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

/// <summary>A plugin loaded into a load context of its own.</summary>
public sealed class LoadedPlugin
{
    private PluginLoadContext? _context;

    private LoadedPlugin(InstalledPlugin installed, PluginLoadContext context, Assembly assembly)
    {
        Installed = installed;
        _context = context;
        Assembly = assembly;
        Unloaded = new WeakReference(context);
    }

    /// <summary>Where it was installed from, and how it was checked.</summary>
    public InstalledPlugin Installed { get; }

    /// <summary>The plugin's own assembly, whose modules and message types pipelines can use.</summary>
    public Assembly Assembly { get; }

    /// <summary>The plugin's load context, weakly: once unloading is done, it stops being alive.</summary>
    public WeakReference Unloaded { get; }

    internal int Holds { get; set; }

    internal static LoadedPlugin Load(InstalledPlugin installed)
    {
        var context = new PluginLoadContext(installed.ToString(), installed.MainAssembly);
        try
        {
            return new LoadedPlugin(installed, context, context.LoadFromAssemblyPath(installed.MainAssembly));
        }
        catch (Exception exception) when (exception is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            context.Unload();
            throw new PluginException($"Couldn't load {installed}: {exception.Message}", exception);
        }
    }

    internal void Unload()
    {
        _context?.Unload();
        _context = null;
    }

    /// <summary>The plugin, as its <c>#plugin</c> line names it.</summary>
    public override string ToString() => Installed.ToString();
}

/// <summary>
/// A plugin's own load context, which can unload. The plugin shares <c>Meep2.SDK</c> and the core modules with the
/// host, so its messages are the host's <c>Message</c>, and .NET's own assemblies. Everything else comes from the
/// plugin's folder, so two plugins can use different versions of a library.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> s_shared = new(["Meep2.SDK", "Meep2.Modules"], StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<HashSet<string>> s_framework = new(FrameworkAssemblies);

    private readonly string _folder;
    private readonly AssemblyDependencyResolver? _resolver;

    public PluginLoadContext(string name, string mainAssembly)
        : base(name, isCollectible: true)
    {
        _folder = Path.GetDirectoryName(mainAssembly)!;
        if (File.Exists(Path.ChangeExtension(mainAssembly, ".deps.json")))
            _resolver = new AssemblyDependencyResolver(mainAssembly);
    }

    /// <summary>Whether an assembly comes from the host rather than the plugin: Meep's SDK and core modules, and .NET's own.</summary>
    public static bool IsShared(string name) => s_shared.Contains(name) || s_framework.Value.Contains(name);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { } name || IsShared(name))
            return null;
        var path = _resolver?.ResolveAssemblyToPath(assemblyName) ?? Probe(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
        _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;

    private string? Probe(string name)
    {
        var path = Path.Combine(_folder, name + ".dll");
        return File.Exists(path) ? path : null;
    }

    // The assemblies of .NET's shared frameworks (the runtime, and ASP.NET Core, which the core modules use).
    private static HashSet<string> FrameworkAssemblies()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shared = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(typeof(object).Assembly.Location)));
        if (shared is null || AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string trusted)
            return names;
        foreach (var path in trusted.Split(Path.PathSeparator))
        {
            if (path.StartsWith(shared, StringComparison.Ordinal))
                names.Add(Path.GetFileNameWithoutExtension(path));
        }
        return names;
    }
}
