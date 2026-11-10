using Meep2.Meeplang.Editing;
using Meep2.Meeplang.Hosting;
using Meep2.Meeplang.Loading;
using Meep2.Plugins;
using Meep2.Runtime;

namespace Meep2.Daemon;

/// <summary>
/// A pipeline the daemon hosts: the bootstrap pipeline that loads and hosts it, and, once it's built, the pipeline
/// itself. One that ends on its own stays listed for a while, with its final counts and why it stopped (Q111).
/// </summary>
public sealed class HostedPipeline
{
    private readonly Lock _lock = new();
    private BuiltPipeline? _built;
    private string _state = PipelineStates.Starting;
    private string? _reason;
    private string? _failure;
    private bool _stopRequested;
    private PipelineDetail? _final;
    private readonly SemaphoreSlim _reloading = new(1, 1);
    private PipelineWatcher? _watcher;
    private readonly List<(string Edit, string? Commit)> _unsaved = [];

    internal HostedPipeline(string name, PipelineAddress address, ModuleRegistry registry, PluginOptions plugins)
    {
        Name = name;
        Address = address;
        Path = address.ToString();
        BaseRegistry = registry;
        Plugins = plugins;
    }

    /// <summary>Where it came from: a file, a URL, Git, standard input or text sent to the daemon (goal 7).</summary>
    public PipelineAddress Address { get; }

    /// <summary>The pipeline as one that can be changed while it runs, once it's built (goal 4).</summary>
    public LivePipeline? Live
    {
        get
        {
            lock (_lock)
                return _built?.Live;
        }
    }

    /// <summary>Whether edits made through the API make its text differ from its file as last loaded.</summary>
    public bool Edited { get; internal set; }

    /// <summary>Whether it reloads whenever its file changes.</summary>
    public bool Watching => _watcher is not null;

    /// <summary>The verbs it has without its plugins.</summary>
    internal ModuleRegistry BaseRegistry { get; }

    /// <summary>Where its plugins come from.</summary>
    internal PluginOptions Plugins { get; }

    /// <summary>Its name: the file's name without <c>.meep</c>.</summary>
    public string Name { get; }

    /// <summary>The file it came from.</summary>
    public string Path { get; }

    /// <summary>When the daemon started loading it.</summary>
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

    /// <summary>When it ended, or null while it's starting, running or restarting.</summary>
    public DateTimeOffset? Ended { get; private set; }

    /// <summary>How many times it has been restarted.</summary>
    public int Restarts { get; private set; }

    /// <summary>The pipeline running now, or the last one to run.</summary>
    public Pipeline? Pipeline
    {
        get
        {
            lock (_lock)
                return _built?.Pipeline;
        }
    }

    /// <summary>One of <see cref="PipelineStates"/>.</summary>
    public string State
    {
        get
        {
            lock (_lock)
                return _state;
        }
    }

    /// <summary>Why it stopped, once it has ended, or why it's being restarted.</summary>
    public string? Reason
    {
        get
        {
            lock (_lock)
                return _reason;
        }
    }

    /// <summary>Warnings from loading it.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_lock)
                return _built is { } built ? [.. built.Warnings.Select(w => w.ToString())] : [];
        }
    }

    /// <summary>How long it stays listed once it has ended on its own, from its file's <c>keep-finished</c>.</summary>
    public TimeSpan KeepFinished
    {
        get
        {
            lock (_lock)
                return _built?.Pipeline.Options.KeepFinished ?? TimeSpan.Zero;
        }
    }

    /// <summary>Whether it has ended and been listed for as long as its file asks.</summary>
    internal bool Expired(DateTimeOffset now) => Ended is { } ended && now - ended >= KeepFinished;

    /// <summary>Whether it has ended because someone asked it to stop, so it needn't stay listed.</summary>
    internal bool StoppedOnRequest
    {
        get
        {
            lock (_lock)
                return _state == PipelineStates.Stopped;
        }
    }

    /// <summary>The verbs and message types it was built with, for tap conditions.</summary>
    internal ModuleRegistry? Registry
    {
        get
        {
            lock (_lock)
                return _built?.Registry;
        }
    }

    /// <summary>Completes when the bootstrap pipeline, and so this pipeline, has finished.</summary>
    internal Task Finished { get; set; } = Task.CompletedTask;

    internal Meep2.Runtime.Pipeline? Bootstrap { get; set; }

    internal void Running(BuiltPipeline built)
    {
        lock (_lock)
        {
            _built = built;
            if (_state != PipelineStates.Stopping)
            {
                _state = PipelineStates.Running;
                _reason = null;
            }
        }
    }

    internal void Restarting(PipelineRestarting restart)
    {
        lock (_lock)
        {
            Restarts = restart.Restart;
            if (_state == PipelineStates.Stopping)
                return;
            _state = PipelineStates.Restarting;
            _reason = $"{restart.Reason}. It starts again at {DateTimeOffset.Now.Add(restart.Delay):HH:mm:ss}";
        }
    }

    /// <summary>Records why it failed, as the host reported it.</summary>
    internal void Failed(string reason)
    {
        lock (_lock)
            _failure ??= reason;
    }

    internal void Stop()
    {
        Pipeline? pipeline;
        lock (_lock)
        {
            _stopRequested = true;
            if (Ended is null)
                _state = PipelineStates.Stopping;
            pipeline = _built?.Pipeline;
        }
        pipeline?.Stop();
        Bootstrap?.Stop(); // also ends a wait to restart
    }

    /// <summary>
    /// Reads its file again and changes the running pipeline to match, plugins included (Q107). Unless
    /// <paramref name="force"/> is set, it refuses if that would undo edits made through the API that aren't in the
    /// file yet (Q120).
    /// </summary>
    /// <exception cref="DaemonException">Reloading would undo edits, so it says which and how to go ahead.</exception>
    internal async Task<LiveChange> ReloadAsync(bool force, CancellationToken cancellationToken)
    {
        var live = Live ?? throw new DaemonException(409, $"{Name} isn't running yet, so it can't be reloaded.");
        await _reloading.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && WouldUndoEdits() is { } warning)
                throw new DaemonException(409, warning);
            var change = await Reloading.ReloadAsync(live, Address, BaseRegistry, Plugins, cancellationToken).ConfigureAwait(false);
            if (change.Applied)
            {
                Edited = false;
                lock (_lock)
                    _unsaved.Clear();
            }
            return change;
        }
        finally
        {
            _reloading.Release();
        }
    }

    /// <summary>Records edits made through the API: committed to a branch, or not saved at all, so not in the file yet.</summary>
    internal void EditedApart(IEnumerable<string> edits, string? commit)
    {
        Edited = true;
        lock (_lock)
            _unsaved.AddRange(edits.Select(edit => (edit, commit)));
    }

    /// <summary>Records edits made through the API and written to the file, which a reload then keeps (Q124).</summary>
    internal void EditedInFile()
    {
        Edited = false;
        lock (_lock)
            _unsaved.Clear();
    }

    // What a reload from the file would undo, as a warning, or null if nothing: edits made through the API that went
    // to a branch that hasn't been merged into the one checked out, or weren't saved.
    private string? WouldUndoEdits()
    {
        List<(string Edit, string? Commit)> unsaved;
        lock (_lock)
            unsaved = [.. _unsaved];
        if (unsaved.Count == 0)
            return null;
        var commits = unsaved.Select(u => u.Commit).Distinct().ToList();
        if (Address.IsFile && commits.All(commit => commit is not null && Meep2.Git.PendingBranch.IsMerged(Path, commit)))
        {
            lock (_lock)
                _unsaved.Clear();
            return null; // they've been merged, so the file has them
        }
        var where = commits.Where(c => c is not null).Select(c => c![..7]).ToList();
        var saved = where.Count > 0 ? $" They're committed as {string.Join(", ", where)}; merge them into your branch to keep them." : " They weren't saved anywhere.";
        return $"Reloading {Name} from its file would undo edits made through meep edit that aren't in the file: {string.Join("; ", unsaved.Select(u => u.Edit))}.{saved} To reload anyway: meep reload {Name} --force";
    }

    /// <summary>Reloads the pipeline whenever its file changes, once the changes settle, telling <paramref name="log"/> what happened.</summary>
    internal void Watch(Action<string> log) =>
        _watcher = new PipelineWatcher(Path, Name, token => ReloadAsync(force: false, token), log, () => Live?.Syntax.Included.Select(i => i.Source.Path) ?? []);

    /// <summary>A change in a sentence: what was added, removed, reconfigured and replaced.</summary>
    internal static string Summary(PipelineChangeReport report)
    {
        var parts = new List<string>();
        void Part(string what, IReadOnlyList<Meep2.SDK.Modules.ModuleInfo> modules)
        {
            if (modules.Count > 0)
                parts.Add($"{what} {string.Join(", ", modules)}");
        }
        Part("added", report.Added);
        Part("removed", report.Removed);
        Part("reconfigured", report.Reconfigured);
        Part("replaced", report.Replaced);
        return parts.Count == 0 ? "nothing changed" : string.Join("; ", parts);
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>Records how it ended, with its final counts.</summary>
    internal void End()
    {
        StopWatching();
        var final = DetailNow();
        lock (_lock)
        {
            Ended = DateTimeOffset.UtcNow;
            (_state, _reason) = _stopRequested ? (PipelineStates.Stopped, "it was asked to stop")
                : _failure is not null ? (PipelineStates.Failed, _failure)
                : (PipelineStates.Finished, "its sources finished");
            _final = final with { State = _state, Ended = Ended, Reason = _reason, Restarts = Restarts };
        }
    }

    internal PipelineSummary Summary()
    {
        lock (_lock)
            return new(Name, Path, _state, Started, Warnings) { Ended = Ended, Reason = _reason, Restarts = Restarts };
    }

    internal PipelineDetail Detail()
    {
        lock (_lock)
        {
            if (_final is not null)
                return _final;
        }
        return DetailNow();
    }

    private PipelineDetail DetailNow()
    {
        var pipeline = Pipeline;
        lock (_lock)
        {
            return new PipelineDetail(
                Name,
                Path,
                _state,
                Started,
                pipeline is not null
                    ? [.. pipeline.Modules.Select(module => new ModuleDetail(module.Number, module.ToString(), module.Verb, module.Name, pipeline.GetStats(module)))]
                    : [])
            {
                Ended = Ended,
                Reason = _reason,
                Restarts = Restarts,
            };
        }
    }
}

/// <summary>The states a hosted pipeline can be in.</summary>
public static class PipelineStates
{
    /// <summary>Being loaded.</summary>
    public const string Starting = "starting";

    /// <summary>Running.</summary>
    public const string Running = "running";

    /// <summary>Failed, and waiting to start again, as its error policy says.</summary>
    public const string Restarting = "restarting";

    /// <summary>Asked to stop, and letting its messages finish.</summary>
    public const string Stopping = "stopping";

    /// <summary>Ended because its sources finished.</summary>
    public const string Finished = "finished";

    /// <summary>Ended because its error policy stopped it, or it failed, with no restarts left.</summary>
    public const string Failed = "failed";

    /// <summary>Ended because someone asked it to stop.</summary>
    public const string Stopped = "stopped";
}
