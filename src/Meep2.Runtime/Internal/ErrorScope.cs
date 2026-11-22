using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>Reports an error that nothing handled, once it and its descendants have finished.</summary>
internal sealed class ErrorScope(ErrorMessage error, Pipeline pipeline) : TrackingScope
{
    protected override void OnFinished(MessageOutcome outcome)
    {
        if (!error.IsHandled)
            pipeline.ReportUnhandled(error);
    }
}
