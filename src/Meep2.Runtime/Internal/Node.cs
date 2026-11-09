using System.Globalization;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>One inflection of a module: how to make it, and whether it takes a message, if its parameters decide.</summary>
/// <param name="Info">The inflection's identity, recorded on the messages it emits.</param>
/// <param name="Create">Makes the inflection.</param>
/// <param name="WhyNot">For each message, why the inflection can't take it, or null if it can. Null takes them all.</param>
internal sealed record InflectionSpec(ModuleInfo Info, Func<AModule> Create, Func<Message, string?>? WhyNot)
{
    /// <summary>The parameters it's made with, if it has a record of them, for <see cref="Reconfigure"/>.</summary>
    public object? Parameters { get; init; }

    /// <summary>
    /// Offers a running instance new parameters: the instance, its old parameters and the new ones. True if it took
    /// them (<see cref="IReconfigurable{TParameters}"/>). Null if the module can't take new parameters.
    /// </summary>
    public Func<AModule, object?, object?, bool>? Reconfigure { get; init; }
}

/// <summary>How a module was added to a pipeline.</summary>
/// <param name="Info">The module's identity.</param>
/// <param name="Inflections">Its inflections, in the order they're tried when several take a message equally well. A source has one.</param>
/// <param name="Options">How the runtime treats it.</param>
/// <param name="IsSource">Whether it's a source.</param>
internal sealed record NodeSpec(ModuleInfo Info, InflectionSpec[] Inflections, NodeOptions Options, bool IsSource);

/// <summary>One module's place in a running pipeline: its instances, its downstream modules and its counts.</summary>
internal abstract class Node(Pipeline pipeline, NodeSpec spec)
{
    private long _received;
    private long _handled;
    private long _emitted;
    private long _dropped;
    private long _filtered;
    private long _failed;
    private long _restarts;
    private long _expired;
    private long _refused;
    private readonly Lock _tapping = new();
    private volatile DebugTap[]? _taps;

    private NodeSpec _spec = spec;

    public Pipeline Pipeline { get; } = pipeline;

    /// <summary>The message the module is handling now and the span tracing it, if tracing is on (OpenTelemetry).</summary>
    public (Message Message, System.Diagnostics.Activity Span)? Handling { get; set; }

    /// <summary>How the module was added. A change to the running pipeline can replace it (goal 4).</summary>
    public NodeSpec Spec
    {
        get => _spec;
        protected set
        {
            _spec = value;
            Label = value.Info.ToString();
        }
    }

    public ModuleInfo Info => Spec.Info;

    /// <summary>The module as people see it, as in "Fetch #2", kept so metrics don't make it again for each message.</summary>
    public string Label { get; private set; } = spec.Info.ToString();

    /// <summary>Whether a change to the running pipeline has taken the module out.</summary>
    public bool IsRemoved { get; protected set; }

    public NodeOptions Options => Spec.Options;

    /// <summary>The error policy for this module: its own, or the pipeline's.</summary>
    public ErrorPolicy Policy => Options.ErrorPolicy ?? Pipeline.Options.ErrorPolicy;

    /// <summary>The modules this one feeds.</summary>
    public InflectionNode[] Downstream { get; set; } = [];

    /// <summary>Makes the module's instances. Called when the pipeline starts.</summary>
    public abstract void CreateModule();

    /// <summary>Disposes the module's instances when the pipeline finishes.</summary>
    public abstract ValueTask DisposeModuleAsync();

    /// <summary>
    /// Takes a new spec for the same module, as when its line was edited: new options, identity and factories, and if
    /// <paramref name="reconfigure"/> is set, new parameters for its running instances, which keep their state if they
    /// take them (<see cref="IReconfigurable{TParameters}"/>) and are replaced otherwise.
    /// </summary>
    /// <returns>Whether every instance kept its state.</returns>
    public abstract Task<bool> UpdateAsync(NodeSpec next, bool reconfigure);

    /// <summary>Offers an instance new parameters, returning whether it took them. A hook that throws counts as no.</summary>
    protected bool TryReconfigure(AModule module, InflectionSpec old, InflectionSpec next)
    {
        if (next.Reconfigure is not { } reconfigure)
            return false;
        try
        {
            return reconfigure(module, old.Parameters, next.Parameters);
        }
        catch (Exception exception)
        {
            Pipeline.ReportFault(next.Info, $"{Describe(next.Info)} couldn't take its new parameters, so it's replaced", exception);
            return false;
        }
    }

    public ModuleStats GetStats() => new(
        Interlocked.Read(ref _received),
        Interlocked.Read(ref _handled),
        Interlocked.Read(ref _emitted),
        Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _filtered),
        Interlocked.Read(ref _failed),
        Interlocked.Read(ref _restarts),
        Interlocked.Read(ref _expired),
        Interlocked.Read(ref _refused),
        Queued);

    /// <summary>How many messages are waiting for the module now.</summary>
    public virtual long Queued => 0;

    /// <summary>
    /// Shows a message the module emitted or passed on to any taps on it. With none, this is one check of a field,
    /// so a pipeline nobody is watching doesn't slow down.
    /// </summary>
    public void Tapped(Message message)
    {
        if (_taps is { } taps)
        {
            foreach (var tap in taps)
                tap.Offer(message);
        }
    }

    public void AddTap(DebugTap tap)
    {
        lock (_tapping)
            _taps = [.. _taps ?? [], tap];
    }

    public void RemoveTap(DebugTap tap)
    {
        lock (_tapping)
        {
            var rest = (_taps ?? []).Where(t => t != tap).ToArray();
            _taps = rest.Length == 0 ? null : rest;
        }
    }

    /// <summary>Ends every tap on the module, as when the pipeline finishes.</summary>
    public void CloseTaps()
    {
        DebugTap[]? taps;
        lock (_tapping)
        {
            taps = _taps;
            _taps = null;
        }
        foreach (var tap in taps ?? [])
            tap.Complete();
    }

    public void CountReceived() => Interlocked.Increment(ref _received);

    public void CountHandled() => Interlocked.Increment(ref _handled);

    public void CountEmitted() => Interlocked.Increment(ref _emitted);

    public void CountDropped() => Interlocked.Increment(ref _dropped);

    public void CountFiltered() => Interlocked.Increment(ref _filtered);

    public void CountFailed() => Interlocked.Increment(ref _failed);

    public void CountRestart() => Interlocked.Increment(ref _restarts);

    public void CountExpired() => Interlocked.Increment(ref _expired);

    public void CountRefused() => Interlocked.Increment(ref _refused);

    protected static AModule CreateInstance(InflectionSpec inflection)
    {
        AModule? module;
        try
        {
            module = inflection.Create();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Couldn't create {Describe(inflection.Info)}: {exception.Message}", exception);
        }
        return module ?? throw new InvalidOperationException($"Couldn't create {Describe(inflection.Info)}: its factory returned null.");
    }

    protected async ValueTask DisposeInstanceAsync(AModule? module, string problem)
    {
        try
        {
            switch (module)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch (Exception exception)
        {
            Pipeline.ReportFault(Info, problem, exception);
        }
    }

    /// <summary>A module, with its inflection if it's one of several: "Fetch #2 (HTTP)".</summary>
    protected static string Describe(ModuleInfo info) =>
        info.Inflection is { } inflection && inflection != info.Verb ? $"{info} ({inflection})" : info.ToString();

    /// <summary>A duration as Meeplang writes it: 250ms, 5s, 2m.</summary>
    internal static string Describe(TimeSpan duration) =>
        duration.TotalMilliseconds < 1000 ? $"{duration.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}ms"
        : duration.TotalSeconds < 60 ? $"{duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s"
        : duration.TotalMinutes < 60 ? $"{duration.TotalMinutes.ToString("0.###", CultureInfo.InvariantCulture)}m"
        : $"{duration.TotalHours.ToString("0.###", CultureInfo.InvariantCulture)}h";
}
