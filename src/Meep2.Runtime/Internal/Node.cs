using System.Globalization;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>One inflection of a module: how to make it, and whether it takes a message, if its parameters decide.</summary>
/// <param name="Info">The inflection's identity, recorded on the messages it emits.</param>
/// <param name="Create">Makes the inflection.</param>
/// <param name="WhyNot">For each message, why the inflection can't take it, or null if it can. Null takes them all.</param>
internal sealed record InflectionSpec(ModuleInfo Info, Func<AModule> Create, Func<Message, string?>? WhyNot);

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

    public Pipeline Pipeline { get; } = pipeline;

    public NodeSpec Spec { get; } = spec;

    public ModuleInfo Info => Spec.Info;

    public NodeOptions Options => Spec.Options;

    /// <summary>The error policy for this module: its own, or the pipeline's.</summary>
    public ErrorPolicy Policy => Options.ErrorPolicy ?? Pipeline.Options.ErrorPolicy;

    /// <summary>The modules this one feeds.</summary>
    public InflectionNode[] Downstream { get; set; } = [];

    /// <summary>Makes the module's instances. Called when the pipeline starts.</summary>
    public abstract void CreateModule();

    /// <summary>Disposes the module's instances when the pipeline finishes.</summary>
    public abstract ValueTask DisposeModuleAsync();

    public ModuleStats GetStats() => new(
        Interlocked.Read(ref _received),
        Interlocked.Read(ref _handled),
        Interlocked.Read(ref _emitted),
        Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _filtered),
        Interlocked.Read(ref _failed),
        Interlocked.Read(ref _restarts),
        Interlocked.Read(ref _expired),
        Interlocked.Read(ref _refused));

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
