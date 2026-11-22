using Meep2.Plugins;

namespace Meep2.Cli;

/// <summary>What every meep command uses: the plugin settings, where to write, and Ctrl+C.</summary>
/// <param name="Plugins">Where plugins are kept, and whether developer mode is on.</param>
/// <param name="Output">Where to write what happened.</param>
/// <param name="Error">Where to write what went wrong.</param>
/// <param name="Cancellation">Cancelled by Ctrl+C.</param>
internal sealed record CommandContext(PluginOptions Plugins, TextWriter Output, TextWriter Error, CancellationToken Cancellation)
{
    /// <summary>The plugin settings, with developer mode on if <paramref name="developerMode"/> is.</summary>
    public PluginOptions Options(bool developerMode = false) => Plugins with { DeveloperMode = Plugins.DeveloperMode || developerMode };
}
