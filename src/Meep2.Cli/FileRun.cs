using Meep2.Daemon;
using Meep2.Meeplang.Hosting;
using Meep2.Meeplang.Loading;
using Meep2.Modules;
using Meep2.Plugins;
using Meep2.Runtime;

namespace Meep2.Cli;

/// <summary>
/// <c>meep run</c>'s pipeline, run here: the fixed bootstrap pipeline reads the file, parses it, installs its plugins,
/// builds the pipeline and hosts it, and with <c>--watch</c> the running pipeline follows its file (goal 4). Its
/// exit code says how it ended.
/// </summary>
/// <param name="context">The plugin settings, where to write, and Ctrl+C.</param>
/// <param name="file">The pipeline's file or address.</param>
/// <param name="watch">Whether to reload it whenever its file changes.</param>
/// <param name="hostId">The ID of this run as a Meep host, if it has one.</param>
internal sealed class FileRun(CommandContext context, string file, bool watch, string? hostId)
{
    private readonly ModuleRegistry _registry = new ModuleRegistry().AddAssembly(CoreModules.Assembly);
    private PipelineWatcher? _watcher;
    private int _exitCode;

    /// <summary>Runs the pipeline until it ends or Ctrl+C. Returns 0, or 1 if it didn't load, 2 for a bad address, 3 if it stopped on an error.</summary>
    public async Task<int> RunAsync()
    {
        _registry.Add<WriteLine, WriteLineParameters>("WriteLine", parameters => new WriteLine(parameters, context.Output));

        // A file on this machine is read as it is; anything else, from its address, checked as it must be (goal 7).
        PipelineAddress address;
        try
        {
            address = PipelineAddress.Parse(file);
        }
        catch (FormatException exception)
        {
            Log(exception.Message);
            return 2;
        }
        if (watch && !address.IsFile)
        {
            Log($"Only a file on this machine can be watched, and {file} isn't one.");
            return 2;
        }
        try
        {
            await using var pipeline = Bootstrap(address).Build();
            await pipeline.RunAsync(context.Cancellation).ConfigureAwait(false);
            return _exitCode;
        }
        finally
        {
            _watcher?.Dispose();
        }
    }

    private PipelineBuilder Bootstrap(PipelineAddress address)
    {
        var plugins = context.Plugins;
        var hosted = HostedOptions();
        var builder = new PipelineBuilder("bootstrap", BootstrapOptions());
        builder.Chain(
            address.IsFile
                ? builder.AddSource(() => new Read(new ReadParameters { Path = file }))
                : builder.AddSource(() => new ReadPipeline(address, plugins, Console.In)),
            builder.Add(() => new Parse()),
            builder.Add(() => new Install(_registry, new PluginInstaller(plugins))),
            builder.Add(() => new Build(_registry, hosted)),
            builder.Add(() => new Host(context.Error, running: Running)));
        return builder;
    }

    // Once the pipeline runs: tells meep run --detach so, and with --watch, starts following its file.
    private void Running(BuiltPipeline built)
    {
        Detaching.Running();
        if (watch && _watcher is null && built.Live is { } live)
        {
            var path = Path.GetFullPath(file);
            _watcher = new PipelineWatcher(path, built.Pipeline.Name, token => Reloading.ReloadAsync(live, path, _registry, context.Plugins, token), Log, () => live.Syntax.Included.Select(i => i.Source.Path));
            Log($"Watching {path} for changes.");
        }
    }

    // The pipeline's own options: its errors and notices go to standard error.
    private PipelineOptions HostedOptions() => new()
    {
        HostId = hostId,
        UnhandledError = e => Log(e.AnsweredBy is { } answerer
            ? $"error from {e.EmittedBy?.ToString() ?? "the pipeline"}, answered by {answerer}: {e.Error}"
            : $"error from {e.EmittedBy?.ToString() ?? "the pipeline"}: {e.Error}"),
        Notice = Log,
    };

    // The bootstrap's: why loading failed, or why the pipeline stopped, sets the exit code.
    private PipelineOptions BootstrapOptions() => new()
    {
        UnhandledError = e =>
        {
            switch (e)
            {
                case PipelineStoppedMessage:
                    Fail(3, text: null); // the pipeline's notice already said why
                    break;
                case MeeplangErrorMessage meeplang:
                    Fail(1, string.Join(Environment.NewLine, meeplang.Diagnostics));
                    break;
                default:
                    Fail(1, $"error: {e.Error}");
                    break;
            }
        },
        Notice = Log,
    };

    private void Log(string text)
    {
        lock (context.Error)
            context.Error.WriteLine(text);
    }

    private void Fail(int code, string? text)
    {
        Interlocked.CompareExchange(ref _exitCode, code, 0);
        if (text is not null)
            Log(text);
    }
}
