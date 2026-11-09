using System.Runtime.ExceptionServices;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Hosting;

/// <summary>A parsed pipeline file, as <see cref="Parse"/> emits it.</summary>
/// <param name="Syntax">The parsed file, including any warnings.</param>
public sealed record ParsedPipeline(PipelineSyntax Syntax) : Message
{
    /// <summary>
    /// The verbs this pipeline can use, when it needs its own, as a pipeline with plugins does. Null means the ones
    /// <see cref="Build"/> was given.
    /// </summary>
    public ModuleRegistry? Registry { get; init; }

    /// <summary>
    /// What keeps the pipeline's plugins loaded. The pipeline lets go of them when it's finished with, so they can
    /// unload.
    /// </summary>
    public IReadOnlyList<IDisposable> Holds { get; init; } = [];

    /// <summary>Warnings found after parsing, such as a plugin loaded without a signature in developer mode.</summary>
    public IReadOnlyList<Diagnostic> Warnings { get; init; } = [];
}

/// <summary>A pipeline ready to run, as <see cref="Build"/> emits it.</summary>
/// <param name="Pipeline">The pipeline.</param>
/// <param name="Warnings">Warnings from parsing and loading.</param>
public sealed record BuiltPipeline(Pipeline Pipeline, IReadOnlyList<Diagnostic> Warnings) : Message
{
    /// <summary>What keeps the pipeline's plugins loaded, let go of once the pipeline has finished.</summary>
    public IReadOnlyList<IDisposable> Holds { get; init; } = [];

    /// <summary>The verbs and message types the pipeline was built with, its plugins' included, for conditions on its messages.</summary>
    public ModuleRegistry? Registry { get; init; }

    /// <summary>Builds the same pipeline again, to restart it (Q112). Null if it can't be.</summary>
    public Func<Pipeline>? Rebuild { get; init; }

    /// <summary>This, with a newly built pipeline in place of the one that ran.</summary>
    internal BuiltPipeline Rebuilt() => new(Rebuild!(), Warnings) { Holds = Holds, Registry = Registry, Rebuild = Rebuild };
}

/// <summary>A pipeline that failed, which <see cref="Host"/> is about to restart, as its error policy says (Q112).</summary>
/// <param name="Name">The pipeline's name.</param>
/// <param name="Reason">Why it stopped, as in "it threw: …" or the error policy's reason.</param>
/// <param name="Delay">How long until it starts again.</param>
/// <param name="Restart">Which restart this is, counting from 1.</param>
/// <param name="Policy">The policy restarting it.</param>
public sealed record PipelineRestarting(string Name, string Reason, TimeSpan Delay, int Restart, PipelineRestart Policy)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        var window = Policy.Within is { } within ? $" in {Durations.Format(within)}" : "";
        return $"{Name} was stopped, because {Reason}. Restarting it in {Durations.Format(Delay)} (restart {Restart} of at most {Policy.Times}{window}).";
    }
}

/// <summary>Problems that stopped a pipeline file loading.</summary>
public sealed record MeeplangErrorMessage : ErrorMessage
{
    /// <summary>Every error and warning found.</summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>The error for a set of diagnostics, described by its errors.</summary>
    public static MeeplangErrorMessage From(IReadOnlyList<Diagnostic> diagnostics) => new()
    {
        Error = string.Join(Environment.NewLine, diagnostics.Where(d => d.IsError)),
        Diagnostics = diagnostics,
    };
}

/// <summary>A pipeline that its error policy stopped, as <see cref="Host"/> reports it.</summary>
public sealed record PipelineStoppedMessage : ErrorMessage
{
    /// <summary>Why the error policy stopped it.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Parses Meeplang text into a <see cref="ParsedPipeline"/>: the lexer and parser, as a module (goal 8).
/// Text with errors becomes a <see cref="MeeplangErrorMessage"/>.
/// </summary>
public sealed class Parse : AInflection<IHasText>
{
    /// <inheritdoc/>
    protected override ValueTask HandleAsync(IHasText message, ModuleContext context, CancellationToken cancellationToken)
    {
        var path = message is IHasPath file ? file.Path : "pipeline";
        var syntax = MeeplangParser.Parse(new SourceFile(path, message.Text));
        context.Emit(syntax.HasErrors ? MeeplangErrorMessage.From(syntax.Diagnostics) : new ParsedPipeline(syntax));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Loads a <see cref="ParsedPipeline"/> into a <see cref="BuiltPipeline"/>, ready to run: the loader, as a module.
/// A file that can't load becomes a <see cref="MeeplangErrorMessage"/>.
/// </summary>
/// <param name="registry">The verbs pipelines can use.</param>
/// <param name="options">Settings for the pipelines it builds.</param>
public sealed class Build(ModuleRegistry registry, PipelineOptions? options = null) : AInflection<ParsedPipeline>
{
    /// <summary>Lets go of a pipeline's plugins, as when it doesn't load or has finished.</summary>
    public static void Release(IReadOnlyList<IDisposable> holds)
    {
        ArgumentNullException.ThrowIfNull(holds);
        foreach (var hold in holds)
            hold.Dispose();
    }

    /// <inheritdoc/>
    protected override ValueTask HandleAsync(ParsedPipeline message, ModuleContext context, CancellationToken cancellationToken)
    {
        LoadResult result;
        try
        {
            result = new PipelineLoader(message.Registry ?? registry).Load(message.Syntax, options);
        }
        catch
        {
            Release(message.Holds);
            throw;
        }
        if (result.Pipeline is { } pipeline)
        {
            var used = message.Registry ?? registry;
            context.Emit(new BuiltPipeline(pipeline, [.. message.Warnings, .. result.Diagnostics.Where(d => !d.IsError)])
            {
                Holds = message.Holds,
                Registry = used,
                Rebuild = () => new PipelineLoader(used).Load(message.Syntax, options).Pipeline
                    ?? throw new InvalidOperationException($"{pipeline.Name} didn't load again, so it can't be restarted."),
            });
        }
        else
        {
            Release(message.Holds);
            context.Emit(MeeplangErrorMessage.From([.. message.Warnings, .. result.Diagnostics]));
        }
        return ValueTask.CompletedTask;
    }
}

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
            Build.Release(message.Holds);
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
