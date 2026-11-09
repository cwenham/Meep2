using Meep2.Meeplang.Hosting;
using Meep2.Meeplang.Loading;
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

    internal HostedPipeline(string name, string path)
    {
        Name = name;
        Path = path;
    }

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

    /// <summary>Records how it ended, with its final counts.</summary>
    internal void End()
    {
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
