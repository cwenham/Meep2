using Meep2.SDK.Security;
using Meep2.Meeplang.Hosting;
using Meep2.Plugins;
using Meep2.SDK.Modules;

namespace Meep2.Daemon;

/// <summary>
/// The bootstrap's first stage for a pipeline from anywhere (goal 7): reads it from its address, checks it can be
/// trusted, and emits it as <see cref="PipelineText"/>, whose includes come from the same place. One that can't be read
/// or trusted becomes an error saying why.
/// </summary>
public sealed class ReadPipeline(PipelineAddress address, PluginOptions plugins, TextReader? stdin = null, string? text = null) : ASource
{
    /// <inheritdoc/>
    protected override async Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        if (text is not null)
        {
            // Sent over the daemon's socket by a client allowed to control it: it's yours, as a file is.
            context.Emit(new PipelineText(address.Location, text) { Credential = PipelineCredential.Owner("it was sent to the daemon by a client allowed to control it") });
            return;
        }
        try
        {
            context.Emit(await PipelineSources.ReadAsync(address, plugins, stdin, cancellationToken).ConfigureAwait(false));
        }
        catch (PipelineSourceException exception)
        {
            context.EmitError(exception.Message, exception);
        }
    }
}
