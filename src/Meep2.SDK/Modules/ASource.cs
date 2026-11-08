namespace Meep2.SDK.Modules;

/// <summary>
/// A fountainhead: a module with no input that emits messages of its own accord, such as a timer or a listener.
/// </summary>
/// <remarks>
/// A pipeline halts once every source has finished and every message has left it. A source finishes when
/// <see cref="RunAsync"/> returns. Sources that run until stopped should return, or throw
/// <see cref="OperationCanceledException"/>, when the cancellation token fires.
/// </remarks>
public abstract class ASource : AModule
{
    /// <summary>Emits messages through <paramref name="context"/> until done or cancelled.</summary>
    protected abstract Task RunAsync(ModuleContext context, CancellationToken cancellationToken);

    internal Task Run(ModuleContext context, CancellationToken cancellationToken) =>
        RunAsync(context, cancellationToken);
}
