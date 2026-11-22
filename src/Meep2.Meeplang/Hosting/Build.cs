using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Syntax;
using Meep2.Runtime;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Hosting;

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
        Editing.LivePipeline? live;
        IReadOnlyList<Diagnostic> diagnostics;
        var used = message.Registry ?? registry;
        try
        {
            (live, diagnostics) = Editing.LivePipeline.Load(message.Syntax, used, options, message.Holds);
        }
        catch
        {
            Release(message.Holds);
            throw;
        }
        if (live is not null)
        {
            context.Emit(new BuiltPipeline(live.Pipeline, [.. message.Warnings, .. diagnostics.Where(d => !d.IsError)])
            {
                Holds = message.Holds,
                Registry = used,
                Rebuild = live.Rebuild,
                Live = live,
            });
        }
        else
        {
            Release(message.Holds);
            context.Emit(MeeplangErrorMessage.From([.. message.Warnings, .. diagnostics]));
        }
        return ValueTask.CompletedTask;
    }
}
