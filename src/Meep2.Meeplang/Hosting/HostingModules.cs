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
            context.Emit(new BuiltPipeline(pipeline, [.. message.Warnings, .. result.Diagnostics.Where(d => !d.IsError)]) { Holds = message.Holds });
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
/// pipeline is asked to stop: the host, as a module. A pipeline its error policy stopped becomes a
/// <see cref="PipelineStoppedMessage"/>.
/// </summary>
/// <param name="diagnostics">Where to write warnings, if anywhere.</param>
public sealed class Host(TextWriter? diagnostics = null) : AInflection<BuiltPipeline>
{
    /// <inheritdoc/>
    protected override async ValueTask HandleAsync(BuiltPipeline message, ModuleContext context, CancellationToken cancellationToken)
    {
        try
        {
            await using var pipeline = message.Pipeline;
            if (diagnostics is not null)
            {
                foreach (var warning in message.Warnings)
                    await diagnostics.WriteLineAsync(warning.ToString()).ConfigureAwait(false);
            }
            await pipeline.RunAsync(context.Stopping).ConfigureAwait(false);
            if (pipeline.StoppedBecause is { } reason)
                context.Emit(new PipelineStoppedMessage { Error = $"{pipeline.Name} was stopped, because {reason}.", Reason = reason });
        }
        finally
        {
            Build.Release(message.Holds);
        }
    }
}
