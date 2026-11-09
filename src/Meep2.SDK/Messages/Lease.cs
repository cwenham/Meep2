using Meep2.SDK.Modules;

namespace Meep2.SDK.Messages;

/// <summary>
/// A live resource that travels with messages, such as a request waiting for its reply or an open connection.
/// The message refers to the lease, and the runtime owns it (Q9, Q48).
/// </summary>
/// <remarks>
/// <para>
/// A module emits a message with a lease through <see cref="ModuleContext.Emit(Message, Lease)"/>. Messages made from
/// it inherit the lease, so it travels down the fam chain with them. The runtime counts the messages in flight that
/// carry it, and when the last one has left the pipeline (handled, dropped, filtered or expired), it calls
/// <see cref="OnFinishedAsync"/>, which closes the lease unless a subclass does something first, such as send a
/// default reply.
/// </para>
/// <para>
/// Only messages that carry a lease pay for counting. Something other than a message can keep a lease open with
/// <see cref="Hold"/>, as a connection that's still being read does. A module can also close a lease on purpose
/// with <see cref="CloseAsync"/>; later writes then find it closed.
/// </para>
/// </remarks>
public abstract class Lease
{
    private static long s_next;

    private readonly LeaseScope _scope;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _closing;
    private int _emitted;
    private volatile Action<Lease, Exception>? _fault;

    /// <summary>Makes the lease. It stays open at least until the module that made it emits it.</summary>
    protected Lease()
    {
        Number = Interlocked.Increment(ref s_next);
        _scope = new LeaseScope(this);
    }

    /// <summary>A number that tells leases apart, for display: "lease 12 (open)".</summary>
    public long Number { get; }

    /// <summary>Whether the lease has been closed.</summary>
    public bool IsClosed => Volatile.Read(ref _closing) != 0;

    /// <summary>Completes once the lease has closed and its resource has been released.</summary>
    public Task Closed => _closed.Task;

    internal TrackingScope Scope => _scope;

    /// <summary>
    /// Keeps the lease open while something other than a message needs it, such as a connection that's still being
    /// read. Dispose the result to let go.
    /// </summary>
    public IDisposable Hold()
    {
        _scope.AddRef();
        return new Holder(_scope);
    }

    /// <summary>
    /// Closes the lease now and releases its resource, whether or not messages still carry it. Does nothing if it's
    /// already closed.
    /// </summary>
    public async ValueTask CloseAsync()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            await _closed.Task.ConfigureAwait(false);
            return;
        }
        try
        {
            await OnClosedAsync().ConfigureAwait(false);
        }
        finally
        {
            _closed.TrySetResult();
        }
    }

    /// <summary>"lease 12 (open)" or "lease 12 (closed)".</summary>
    public override string ToString() => $"lease {Number} ({(IsClosed ? "closed" : "open")})";

    /// <summary>
    /// Called once, when the last message carrying the lease has left the pipeline and nothing holds it. Closes the
    /// lease; override it to do something first, such as send a reply nobody else sent.
    /// </summary>
    protected virtual ValueTask OnFinishedAsync() => CloseAsync();

    /// <summary>Releases the resource. Called once, when the lease closes.</summary>
    protected abstract ValueTask OnClosedAsync();

    /// <summary>Whether the runtime knows where to report a failure while the lease finishes.</summary>
    internal bool IsAttached => _fault is not null;

    /// <summary>Called by the runtime when the lease is first emitted: where to report a failure while it finishes.</summary>
    internal void Attach(Action<Lease, Exception> fault) => _fault ??= fault;

    /// <summary>Whether this is the lease's first emit, when the hold its maker had is let go.</summary>
    internal bool TakeMakersHold() => Interlocked.Exchange(ref _emitted, 1) == 0;

    /// <summary>The leases in a message's tracking scopes.</summary>
    internal static IEnumerable<Lease> In(object? tracking)
    {
        switch (tracking)
        {
            case null:
                yield break;
            case LeaseScope scope:
                yield return scope.Lease;
                yield break;
            case TrackingScope[] scopes:
                foreach (var each in scopes)
                {
                    if (each is LeaseScope leased)
                        yield return leased.Lease;
                }
                yield break;
        }
    }

    // Finishing does I/O, such as sending a default reply, so it runs off the thread that let go of the last message.
    private async Task FinishAsync()
    {
        try
        {
            await OnFinishedAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _closed.TrySetResult();
            _fault?.Invoke(this, exception);
        }
    }

    private sealed class LeaseScope(Lease lease) : TrackingScope
    {
        public Lease Lease => lease;

        protected override void OnFinished(MessageOutcome outcome) => _ = Task.Run(lease.FinishAsync);
    }

    private sealed class Holder(TrackingScope scope) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                scope.Release();
        }
    }
}
