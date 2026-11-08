using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Hosting;

/// <summary>A parsed pipeline file, as <see cref="Parse"/> emits it.</summary>
/// <param name="Syntax">The parsed file, including any warnings.</param>
public sealed record ParsedPipeline(PipelineSyntax Syntax) : Message;

/// <summary>A pipeline ready to run, as <see cref="Build"/> emits it.</summary>
/// <param name="Pipeline">The pipeline.</param>
/// <param name="Warnings">Warnings from parsing and loading.</param>
public sealed record BuiltPipeline(Pipeline Pipeline, IReadOnlyList<Diagnostic> Warnings) : Message;

/// <summary>Problems that stopped a pipeline file loading.</summary>
public sealed record MeeplangErrorMessage : ErrorMessage
{
    /// <summary>Every error and warning found.</summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    internal static MeeplangErrorMessage From(IReadOnlyList<Diagnostic> diagnostics) => new()
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
    /// <inheritdoc/>
    protected override ValueTask HandleAsync(ParsedPipeline message, ModuleContext context, CancellationToken cancellationToken)
    {
        var result = new PipelineLoader(registry).Load(message.Syntax, options);
        context.Emit(result.Pipeline is { } pipeline
            ? new BuiltPipeline(pipeline, [.. result.Diagnostics.Where(d => !d.IsError)])
            : MeeplangErrorMessage.From(result.Diagnostics));
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
        if (diagnostics is not null)
        {
            foreach (var warning in message.Warnings)
                await diagnostics.WriteLineAsync(warning.ToString()).ConfigureAwait(false);
        }
        await using var pipeline = message.Pipeline;
        await pipeline.RunAsync(context.Stopping).ConfigureAwait(false);
        if (pipeline.StoppedBecause is { } reason)
            context.Emit(new PipelineStoppedMessage { Error = $"{pipeline.Name} was stopped, because {reason}.", Reason = reason });
    }
}
