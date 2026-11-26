using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Syntax;
using Meep2.Runtime;
using Meep2.SDK.Security;
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

    // The options for a pipeline: with the credential where it came from earns it, if it says (Q252), and none if it
    // has the owner's but includes a file from this machine that someone else could have written (Q320).
    private PipelineOptions? OptionsFor(ParsedPipeline message)
    {
        var chosen = options;
        for (var ancestor = message.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is PipelineText { Credential: { } credential })
            {
                chosen = (options ?? PipelineOptions.Default) with { Credential = credential };
                break;
            }
        }
        if (chosen?.Credential is { Vouch: PipelineVouch.Owner })
        {
            foreach (var included in message.Syntax.Included.Select(i => i.Source.Path).Where(File.Exists))
            {
                if (Meep2.Shared.PrivateFolders.WhyNotUsersAlone(included) is { } why)
                    return chosen with { Credential = PipelineCredential.None($"it includes {included}, and {why}") };
            }
        }
        return chosen;
    }

    /// <inheritdoc/>
    protected override ValueTask HandleAsync(ParsedPipeline message, ModuleContext context, CancellationToken cancellationToken)
    {
        Editing.LivePipeline? live;
        IReadOnlyList<Diagnostic> diagnostics;
        var used = message.Registry ?? registry;
        try
        {
            (live, diagnostics) = Editing.LivePipeline.Load(message.Syntax, used, OptionsFor(message), message.Holds);
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
