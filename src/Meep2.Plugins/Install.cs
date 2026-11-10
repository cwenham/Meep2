using Meep2.Meeplang.Hosting;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Syntax;
using Meep2.SDK.Modules;

namespace Meep2.Plugins;

/// <summary>
/// Installs and loads the plugins a parsed pipeline names in its <c>#plugin</c> lines, and gives the pipeline its own
/// verbs: the core ones plus its plugins', which can also be reached by the plugin's name, as in
/// <c>Transit.Departures</c> (Q105). The Install stage of the bootstrap pipeline, between Parse and Build (goal 8). A plugin that can't be installed, or doesn't pass its checks, becomes a
/// <see cref="MeeplangErrorMessage"/> pointing at its line.
/// </summary>
/// <remarks>
/// A pipeline file's NuGet plugins are recorded in its lock file, <c>name.meep.lock</c>, and refused if their packages
/// change (Q103).
/// </remarks>
/// <param name="registry">The verbs every pipeline has.</param>
/// <param name="installer">Installs and checks plugins.</param>
/// <param name="catalog">Loads plugins and shares them between pipelines. The process's own unless given.</param>
public sealed class Install(ModuleRegistry registry, PluginInstaller installer, PluginCatalog? catalog = null) : AInflection<ParsedPipeline>
{
    private readonly PluginCatalog _catalog = catalog ?? PluginCatalog.Shared;

    /// <inheritdoc/>
    protected override async ValueTask HandleAsync(ParsedPipeline message, ModuleContext context, CancellationToken cancellationToken)
    {
        if (message.Syntax.Plugins.Count == 0)
        {
            context.Emit(message);
            return;
        }
        var installed = await InstallAllAsync(message.Syntax, registry, installer, _catalog, saveLock: true, cancellationToken).ConfigureAwait(false);
        if (installed.Problems.Count > 0)
        {
            Build.Release(installed.Holds);
            context.Emit(MeeplangErrorMessage.From([.. installed.Warnings, .. installed.Problems]));
            return;
        }
        context.Emit(message with { Registry = installed.Registry, Holds = [.. message.Holds, .. installed.Holds], Warnings = [.. message.Warnings, .. installed.Warnings] });
    }

    /// <summary>
    /// Installs and loads the plugins a pipeline's <c>#plugin</c> lines name, giving it its own verbs: the core ones
    /// plus its plugins'. The language server uses it too, offline, to check plugins' verbs as you type (Q138).
    /// </summary>
    /// <param name="syntax">The pipeline.</param>
    /// <param name="registry">The verbs every pipeline has.</param>
    /// <param name="installer">Installs and checks plugins.</param>
    /// <param name="catalog">Loads plugins and shares them between pipelines.</param>
    /// <param name="saveLock">Whether to write the pipeline's lock file, which editing doesn't.</param>
    /// <param name="cancellationToken">Stops downloads.</param>
    public static async Task<InstalledPlugins> InstallAllAsync(
        PipelineSyntax syntax, ModuleRegistry registry, PluginInstaller installer, PluginCatalog catalog, bool saveLock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(catalog);
        var path = syntax.Source.Path;
        var file = File.Exists(path) ? Path.GetFullPath(path) : null;
        var baseFolder = file is null ? null : Path.GetDirectoryName(file);
        PluginLock? lockFile = null;
        var own = registry.Clone();
        var holds = new List<IDisposable>();
        var problems = new List<Diagnostic>();
        var warnings = new List<Diagnostic>();
        foreach (var directive in syntax.Plugins)
        {
            PluginHold? hold = null;
            try
            {
                var installed = await installer.InstallAsync(directive, baseFolder, cancellationToken).ConfigureAwait(false);
                if (installed.Packages.Count > 0 && file is not null)
                {
                    lockFile ??= PluginLock.Load(PluginLock.For(file));
                    lockFile.Check(installed);
                }
                if (installed.Warning is { } warning)
                    warnings.Add(new Diagnostic(DiagnosticSeverity.Warning, warning, directive.Span, path));
                hold = catalog.Acquire(installed);
                if (hold.Plugin.RebuiltWithoutNewVersion() is { } stale)
                    warnings.Add(new Diagnostic(DiagnosticSeverity.Warning, stale, directive.Span, path));
                var name = directive.Alias ?? NameOf(hold.Plugin);
                if (own.WhyNotPluginName(name) is { } problem)
                {
                    var hint = directive.Alias is null ? $" Give it another name with as, as in #plugin {directive} as {name}Plugin." : "";
                    throw new PluginException($"{directive} can't be called {name}: {problem}{hint}");
                }
                own.AddPlugin(name, hold.Plugin.Assembly);
                holds.Add(hold);
            }
            catch (Exception exception) when (exception is PluginException or ArgumentException or IOException or UnauthorizedAccessException or HttpRequestException or NuGet.Protocol.Core.Types.FatalProtocolException)
            {
                hold?.Dispose();
                problems.Add(new Diagnostic(DiagnosticSeverity.Error, Describe(directive, exception), directive.Span, path));
            }
        }

        if (saveLock)
        {
            try
            {
                lockFile?.Save();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add(new Diagnostic(DiagnosticSeverity.Warning, $"Couldn't write the lock file {lockFile!.Path}: {exception.Message}", syntax.Plugins[0].Span, path));
            }
        }
        return new InstalledPlugins(own, holds, warnings, problems);
    }

    // A plugin's name, unless its line gives one: the last part of its assembly's, so Meep.Transit is Transit.
    private static string NameOf(LoadedPlugin plugin)
    {
        var assembly = plugin.Assembly.GetName().Name ?? "Plugin";
        return assembly[(assembly.LastIndexOf('.') + 1)..];
    }

    private static string Describe(PluginDirective directive, Exception exception) => exception switch
    {
        PluginException => exception.Message,
        ArgumentException => $"{directive}'s modules can't be added: {exception.Message}",
        _ => $"Couldn't install {directive}: {exception.Message}",
    };
}

/// <summary>What installing a pipeline's plugins gave.</summary>
/// <param name="Registry">The pipeline's own verbs: the core ones plus its plugins'.</param>
/// <param name="Holds">The plugins it holds loaded, released when it's done with them.</param>
/// <param name="Warnings">What to warn about.</param>
/// <param name="Problems">Plugins that couldn't be installed or loaded, at their lines.</param>
public sealed record InstalledPlugins(ModuleRegistry Registry, IReadOnlyList<IDisposable> Holds, IReadOnlyList<Diagnostic> Warnings, IReadOnlyList<Diagnostic> Problems);
