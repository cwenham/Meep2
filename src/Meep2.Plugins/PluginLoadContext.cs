using System.Reflection;
using System.Runtime.Loader;

namespace Meep2.Plugins;

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
