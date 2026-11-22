using System.Collections.Concurrent;
using Meep2.Meeplang.Hosting;
using Meep2.Modules;
using Meep2.Plugins;
using Meep2.Runtime;

namespace Meep2.Daemon;

/// <summary>
/// The pipeline that loads a hosted pipeline and keeps it running: Read (or ReadPipeline), Parse, Install, Build and
/// Host (goal 8). It tells the hosted pipeline when it's running, restarting or has failed, and says whether it loaded,
/// with the problems if it didn't, for the client that asked for it.
/// </summary>
/// <param name="hosted">The pipeline it loads, which also says where from, with which verbs and plugins.</param>
/// <param name="log">Writes a line to the daemon's log.</param>
internal sealed class PipelineBootstrap(HostedPipeline hosted, Action<string> log)
{
    private readonly ConcurrentQueue<string> _problems = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes with true once the pipeline is running, or false if it ended before it was.</summary>
    public Task<bool> Loaded => _ready.Task;

    /// <summary>Why it didn't load.</summary>
    public IReadOnlyList<string> Problems => [.. _problems];

    /// <summary>
    /// Builds the bootstrap, reading the pipeline from <paramref name="text"/> if it was sent, and otherwise from its
    /// address. Its pipeline gets <paramref name="hostId"/>, and its plugins come through <paramref name="catalog"/>.
    /// </summary>
    public Pipeline Build(string? text, PluginCatalog? catalog, string hostId)
    {
        var address = hosted.Address;
        var hostedOptions = HostedOptions(hostId);
        var builder = new PipelineBuilder($"{hosted.Name} (bootstrap)", BootstrapOptions());
        builder.Chain(
            address.IsFile
                ? builder.AddSource(() => new Read(new ReadParameters { Path = address.Location }))
                : builder.AddSource(() => new ReadPipeline(address, hosted.Plugins, text: text)),
            builder.Add(() => new Parse()),
            builder.Add(() => new Install(hosted.BaseRegistry, new PluginInstaller(hosted.Plugins), catalog)),
            builder.Add(() => new Meep2.Meeplang.Hosting.Build(hosted.BaseRegistry, hostedOptions)),
            builder.Add(() => new Host(
                running: built =>
                {
                    hosted.Running(built);
                    if (!_ready.TrySetResult(true))
                        log($"[{hosted.Name}] is running again");
                },
                restarting: restart =>
                {
                    hosted.Restarting(restart);
                    log($"[{hosted.Name}] {restart}");
                })));
        return builder.Build();
    }

    /// <summary>Says the bootstrap has ended. Returns whether the pipeline had loaded, and so has ended now too.</summary>
    public bool End() => !_ready.TrySetResult(false);

    // The hosted pipeline's own options: its errors and notices go to the log.
    private PipelineOptions HostedOptions(string hostId)
    {
        var name = hosted.Name;
        return new PipelineOptions
        {
            HostId = hostId,
            UnhandledError = e => log(e.AnsweredBy is { } answerer
                ? $"[{name}] error from {e.EmittedBy?.ToString() ?? "the pipeline"}, answered by {answerer}: {e.Error}"
                : $"[{name}] error from {e.EmittedBy?.ToString() ?? "the pipeline"}: {e.Error}"),
            Notice = notice => log($"[{name}] {notice}"),
        };
    }

    // The bootstrap's: while loading, its errors are why it didn't load; after, why the pipeline failed.
    private PipelineOptions BootstrapOptions() => new()
    {
        UnhandledError = e =>
        {
            if (!_ready.Task.IsCompleted)
            {
                // Loading failed: the client that asked for it is told why.
                if (e is MeeplangErrorMessage meeplang)
                {
                    foreach (var diagnostic in meeplang.Diagnostics)
                        _problems.Enqueue(diagnostic.ToString());
                }
                else
                {
                    _problems.Enqueue(e.Error);
                }
                return;
            }
            hosted.Failed(e is PipelineStoppedMessage stopped ? stopped.Reason : e.Error);
            log($"[{hosted.Name}] {e.Error}");
        },
        Notice = notice => log($"[{hosted.Name}] {notice}"),
    };
}
