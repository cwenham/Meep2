using System.Runtime.ExceptionServices;
using Meep2.Runtime;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Hosting;

/// <summary>
/// Runs each <see cref="BuiltPipeline"/> it receives until that pipeline halts, or stops it when this module's own
/// pipeline is asked to stop: the host, as a module. A pipeline that its error policy stopped, or that failed, is
/// started again if its policy says to (<see cref="ErrorPolicy.RestartPipeline"/>), and otherwise becomes a
/// <see cref="PipelineStoppedMessage"/>, or for a failure, this module's own error.
/// </summary>
/// <param name="diagnostics">Where warnings and restarts are written, such as standard error, if anywhere.</param>
/// <param name="running">Told about each pipeline once it's running, as the daemon is, so it can watch and stop it. A restarted pipeline is a new one.</param>
/// <param name="restarting">Told when a pipeline is about to be restarted.</param>
public sealed class Host(TextWriter? diagnostics = null, Action<BuiltPipeline>? running = null, Action<PipelineRestarting>? restarting = null)
    : AInflection<BuiltPipeline>
{
    /// <inheritdoc/>
    protected override async ValueTask HandleAsync(BuiltPipeline message, ModuleContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (diagnostics is not null)
            {
                foreach (var warning in message.Warnings)
                    await diagnostics.WriteLineAsync(warning.ToString()).ConfigureAwait(false);
            }
            var current = message;
            var restarts = new List<DateTimeOffset>();
            while (true)
            {
                var (reason, failure) = await RunOnceAsync(current, context).ConfigureAwait(false);
                if (reason is null)
                    return; // it finished, or was asked to stop

                var pipeline = current.Pipeline;
                var policy = pipeline.Options.ErrorPolicy.RestartPipeline;
                if (context.Stopping.IsCancellationRequested || policy is null || current.Rebuild is null || !policy.Allows(restarts, DateTimeOffset.UtcNow))
                {
                    failure?.Throw();
                    var tried = restarts.Count == 0 ? "" : restarts.Count == 1 ? " It had been restarted once." : $" It had been restarted {restarts.Count} times.";
                    context.Emit(new PipelineStoppedMessage { Error = $"{pipeline.Name} was stopped, because {reason}.{tried}", Reason = reason });
                    return;
                }

                restarts.Add(DateTimeOffset.UtcNow);
                var restart = new PipelineRestarting(pipeline.Name, reason, policy.Delay, restarts.Count, policy);
                restarting?.Invoke(restart);
                if (diagnostics is not null)
                    await diagnostics.WriteLineAsync(restart.ToString()).ConfigureAwait(false);
                try
                {
                    await Task.Delay(policy.Delay, context.Stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return; // asked to stop while waiting
                }
                current = current.Rebuilt();
            }
        }
        finally
        {
            // A pipeline changed while it ran may hold other plugins than it started with.
            Build.Release(message.Live?.Holds ?? message.Holds);
        }
    }

    // Runs a pipeline until it halts. Returns why it stopped, if its error policy stopped it or it failed.
    private async Task<(string? Reason, ExceptionDispatchInfo? Failure)> RunOnceAsync(BuiltPipeline built, ModuleContext context)
    {
        await using var pipeline = built.Pipeline;
        try
        {
            var run = pipeline.RunAsync(context.Stopping);
            running?.Invoke(built);
            await run.ConfigureAwait(false);
        }
        catch (Exception exception) when (!context.Stopping.IsCancellationRequested)
        {
            return ($"it failed: {exception.Message}", ExceptionDispatchInfo.Capture(exception));
        }
        return (pipeline.StoppedBecause, null);
    }
}
