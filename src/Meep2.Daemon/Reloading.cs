using Meep2.Meeplang.Editing;
using Meep2.Meeplang.Hosting;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Syntax;
using Meep2.Modules;
using Meep2.Plugins;
using Meep2.Runtime;
using Meep2.SDK.Modules;

namespace Meep2.Daemon;

/// <summary>
/// Reads a running pipeline's file again and changes the pipeline to match (goal 4). Its plugins are installed again
/// too, so one rebuilt on this machine is loaded afresh and its modules replaced (Q107); plugins that haven't changed
/// stay loaded, and their modules carry on.
/// </summary>
public static class Reloading
{
    /// <summary>Reads the file, installs its plugins and changes the running pipeline to match.</summary>
    /// <param name="live">The running pipeline.</param>
    /// <param name="path">Its file.</param>
    /// <param name="registry">The verbs it has without its plugins.</param>
    /// <param name="plugins">Where its plugins come from.</param>
    public static Task<LiveChange> ReloadAsync(LivePipeline live, string path, ModuleRegistry registry, PluginOptions plugins, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return InstallAndChangeAsync(live, path, builder =>
        {
            var parse = builder.Add(() => new Parse());
            builder.Connect(builder.AddSource(() => new Read(new ReadParameters { Path = path })), parse);
            return parse;
        }, registry, plugins, cancellationToken);
    }

    /// <summary>
    /// Changes the running pipeline to new text, as after an edit, installing plugins first if its <c>#plugin</c> lines
    /// have changed (Q125).
    /// </summary>
    public static Task<LiveChange> ApplyAsync(LivePipeline live, PipelineSyntax next, ModuleRegistry registry, PluginOptions plugins, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(next);
        static IEnumerable<string> Plugins(PipelineSyntax syntax) => syntax.Plugins.Select(p => $"{p} as {p.Alias}");
        if (Plugins(next).SequenceEqual(Plugins(live.Syntax)))
            return live.ChangeAsync(next, cancellationToken: cancellationToken);
        return InstallAndChangeAsync(live, next.Source.Path, builder => builder.AddSource(() => new Emitting(new ParsedPipeline(next))), registry, plugins, cancellationToken);
    }

    // Runs the bootstrap's stages up to Install, then changes the running pipeline to what they made.
    private static async Task<LiveChange> InstallAndChangeAsync(
        LivePipeline live, string path, Func<PipelineBuilder, ModuleInfo> stagesBefore, ModuleRegistry registry, PluginOptions plugins, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(plugins);

        var parsed = new TaskCompletionSource<ParsedPipeline>(TaskCreationOptions.RunContinuationsAsynchronously);
        var problems = new List<Diagnostic>();
        var builder = new PipelineBuilder($"{live.Pipeline.Name} (reload)", new PipelineOptions
        {
            UnhandledError = error =>
            {
                lock (problems)
                {
                    if (error is MeeplangErrorMessage meeplang)
                        problems.AddRange(meeplang.Diagnostics);
                    else
                        problems.Add(new Diagnostic(DiagnosticSeverity.Error, error.Error, new SourceSpan(1, 1, 0), path));
                }
            },
            Notice = _ => { },
        });
        var last = stagesBefore(builder);
        var install = builder.Add(() => new Install(registry, new PluginInstaller(plugins)));
        builder.Connect(last, install);
        builder.Connect(install, builder.Add(() => new Capture(parsed)));
        await using (var bootstrap = builder.Build())
            await bootstrap.RunAsync(cancellationToken).ConfigureAwait(false);

        if (!parsed.Task.IsCompleted)
        {
            lock (problems)
                return new LiveChange(false, null, [], [.. problems]);
        }
        var next = await parsed.Task.ConfigureAwait(false);
        var change = await live.ChangeAsync(next.Syntax, next.Registry ?? registry, next.Holds, cancellationToken).ConfigureAwait(false);
        return change with { Diagnostics = [.. next.Warnings, .. change.Diagnostics] };
    }

    /// <summary>What a change did, as the API reports it.</summary>
    public static ChangeResult Describe(LiveChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        static IReadOnlyList<string> Labels(IReadOnlyList<ModuleInfo>? modules) => modules is null ? [] : [.. modules.Select(m => m.ToString())];
        var report = change.Report;
        return new ChangeResult(
            change.Applied,
            Labels(report?.Added),
            Labels(report?.Removed),
            Labels(report?.Reconfigured),
            Labels(report?.Replaced),
            Labels(report?.Unchanged),
            [.. change.Diagnostics.Select(d => d.ToString())])
        {
            SettingsChanged = change.SettingsChanged,
        };
    }

    /// <summary>
    /// An edit as the API names it: set, unset, set-modifier, remove-modifier, rename, add, insert, remove or
    /// remove-branch for a module; set-default, unset-default, set-const, unset-const, add-plugin or remove-plugin for
    /// the rest of the file.
    /// </summary>
    /// <exception cref="DaemonException">There's no such edit, or it needs a module and has none.</exception>
    public static SourceEdit EditOf(EditItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var kind = item.Kind.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "set" => EditKind.Set,
            "unset" => EditKind.Unset,
            "setmodifier" => EditKind.SetModifier,
            "removemodifier" => EditKind.RemoveModifier,
            "rename" => EditKind.Rename,
            "add" => EditKind.Add,
            "insert" => EditKind.Insert,
            "remove" => EditKind.Remove,
            "removebranch" => EditKind.RemoveBranch,
            "setdefault" => EditKind.SetDefault,
            "unsetdefault" => EditKind.UnsetDefault,
            "setconst" or "setconstant" => EditKind.SetConstant,
            "unsetconst" or "unsetconstant" => EditKind.UnsetConstant,
            "addplugin" => EditKind.AddPlugin,
            "removeplugin" => EditKind.RemovePlugin,
            _ => throw new DaemonException(400, $"There's no edit called {item.Kind}. The edits are set, unset, set-modifier, remove-modifier, rename, add, insert, remove and remove-branch for a module, and set-default, unset-default, set-const, unset-const, add-plugin and remove-plugin for the rest of the file."),
        };
        if (kind < EditKind.SetDefault && string.IsNullOrEmpty(item.Module))
            throw new DaemonException(400, $"The edit {item.Kind} needs a module.");
        return new SourceEdit(item.Module ?? "", kind, item.Name, item.Value);
    }

    // The start of an edit's bootstrap: the edited text, already parsed.
    private sealed class Emitting(ParsedPipeline message) : ASource
    {
        protected override Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            context.Emit(message);
            return Task.CompletedTask;
        }
    }

    // The end of the reload's bootstrap: keeps what Install produced.
    private sealed class Capture(TaskCompletionSource<ParsedPipeline> parsed) : AInflection<ParsedPipeline>
    {
        protected override ValueTask HandleAsync(ParsedPipeline message, ModuleContext context, CancellationToken cancellationToken)
        {
            if (!parsed.TrySetResult(message))
                Build.Release(message.Holds);
            return ValueTask.CompletedTask;
        }
    }
}
