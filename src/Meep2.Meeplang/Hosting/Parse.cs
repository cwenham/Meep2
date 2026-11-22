using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Hosting;

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
        var source = new SourceFile(path, message.Text);
        var syntax = message is PipelineText { Includes: { } includes } ? MeeplangParser.Parse(source, includes) : MeeplangParser.Parse(source);
        context.Emit(syntax.HasErrors ? MeeplangErrorMessage.From(syntax.Diagnostics) : new ParsedPipeline(syntax));
        return ValueTask.CompletedTask;
    }
}
