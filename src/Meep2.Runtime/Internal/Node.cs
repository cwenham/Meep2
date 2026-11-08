using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>How a module was added to a pipeline.</summary>
internal sealed record NodeSpec(ModuleInfo Info, Func<AModule> Create, NodeOptions Options, bool IsSource);

/// <summary>One module's place in a running pipeline: its instance, its downstream modules and its counts.</summary>
internal abstract class Node(Pipeline pipeline, NodeSpec spec)
{
    private long _received;
    private long _handled;
    private long _emitted;
    private long _dropped;
    private long _filtered;
    private long _failed;
    private long _restarts;

    public Pipeline Pipeline { get; } = pipeline;

    public NodeSpec Spec { get; } = spec;

    public ModuleInfo Info => Spec.Info;

    public NodeOptions Options => Spec.Options;

    /// <summary>The modules this one feeds.</summary>
    public InflectionNode[] Downstream { get; set; } = [];

    /// <summary>Makes the module instance. Called when the pipeline starts.</summary>
    public abstract void CreateModule();

    /// <summary>Disposes the module instance when the pipeline finishes.</summary>
    public abstract ValueTask DisposeModuleAsync();

    public ModuleStats GetStats() => new(
        Interlocked.Read(ref _received),
        Interlocked.Read(ref _handled),
        Interlocked.Read(ref _emitted),
        Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _filtered),
        Interlocked.Read(ref _failed),
        Interlocked.Read(ref _restarts));

    public void CountReceived() => Interlocked.Increment(ref _received);

    public void CountHandled() => Interlocked.Increment(ref _handled);

    public void CountEmitted() => Interlocked.Increment(ref _emitted);

    public void CountDropped() => Interlocked.Increment(ref _dropped);

    public void CountFiltered() => Interlocked.Increment(ref _filtered);

    public void CountFailed() => Interlocked.Increment(ref _failed);

    public void CountRestart() => Interlocked.Increment(ref _restarts);

    protected AModule CreateInstance()
    {
        AModule? module;
        try
        {
            module = Spec.Create();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Couldn't create {Info}: {exception.Message}", exception);
        }
        return module ?? throw new InvalidOperationException($"Couldn't create {Info}: its factory returned null.");
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
}
