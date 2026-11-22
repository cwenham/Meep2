using System.Reflection;

namespace Meep2.Plugins;

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
        Built = File.GetLastWriteTimeUtc(installed.MainAssembly);
    }

    /// <summary>When the copy that was loaded was built, as its main assembly's time says.</summary>
    public DateTime Built { get; }

    /// <summary>
    /// Why this plugin, on this machine, won't be loaded afresh though it has been rebuilt since: its version is the
    /// same (Q127). Null if it hasn't been rebuilt, or isn't on this machine.
    /// </summary>
    public string? RebuiltWithoutNewVersion()
    {
        if (Installed.Directive.Origin != Meep2.Meeplang.Syntax.PluginOrigin.Local || !File.Exists(Installed.MainAssembly))
            return null;
        return File.GetLastWriteTimeUtc(Installed.MainAssembly) > Built
            ? $"{Installed.Directive.Name} has been rebuilt since it was loaded, but its version is still {PluginInstaller.VersionOf(Installed.MainAssembly)}, so pipelines keep the build they loaded. Give it a new version to load the new build."
            : null;
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
