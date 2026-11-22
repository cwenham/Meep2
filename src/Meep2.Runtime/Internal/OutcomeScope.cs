using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>Reports how a tracked message fared once it and its descendants have finished.</summary>
internal sealed class OutcomeScope : TrackingScope
{
    private readonly TaskCompletionSource<MessageOutcome> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<MessageOutcome> Task => _finished.Task;

    protected override void OnFinished(MessageOutcome outcome) => _finished.TrySetResult(outcome);
}
