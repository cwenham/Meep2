using Meep2.SDK.Modules;

namespace Meep2.SDK.Messages;

/// <summary>
/// Counts the messages in flight that carry this scope: one for each buffer slot or handler a message occupies.
/// Messages made from a tracked message inherit its scopes, so the count reaches zero only once the message and all
/// its descendants have left the pipeline.
/// </summary>
/// <remarks>
/// This is the one mechanism behind tracked emits (acknowledgements and transactions), handled-error detection,
/// and leases on live connections. Ordinary messages carry no scopes and pay nothing for it. It lives in the SDK so
/// that <see cref="Lease"/> can use it; the runtime's own scopes derive from it too.
/// </remarks>
internal abstract class TrackingScope
{
    // Once finished, the count jumps far below zero, so a message re-emitted later (from a cache, say) can come and
    // go without finishing the scope a second time.
    private const int FinishedCount = int.MinValue / 2;

    private int _count = 1; // held by whoever made the scope until the first delivery is done
    private int _outcome;

    public void AddRef() => Interlocked.Increment(ref _count);

    public void Flag(MessageOutcome outcome) => Interlocked.Or(ref _outcome, (int)outcome);

    public void Release()
    {
        if (Interlocked.Decrement(ref _count) == 0 && Interlocked.CompareExchange(ref _count, FinishedCount, 0) == 0)
            OnFinished((MessageOutcome)Volatile.Read(ref _outcome));
    }

    protected abstract void OnFinished(MessageOutcome outcome);
}
