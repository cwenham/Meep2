namespace Meep2.Daemon.Subscriptions;

/// <summary>
/// A subscription's timing: a timer for each thing asked for, all on one grid, so things at 100 ms and 200 ms fall due
/// together; the heartbeat; and the budget, which holds updates back so bytes sent stay under it on average. Times are
/// in <see cref="Environment.TickCount64"/> milliseconds.
/// </summary>
internal sealed class SubscriptionClock
{
    // Each thing asked for, with when it's next due.
    private readonly Dictionary<Slot, long> _slots = [];
    private long _lastSent = Environment.TickCount64;
    private double _allowedAt; // when the budget lets the next update go
    private long? _heldFrom; // when the next update would have gone, had the budget not held it back

    /// <summary>Starts the heartbeat from now, once <c>hello</c> has gone.</summary>
    public void Start() => _lastSent = Environment.TickCount64;

    /// <summary>Everything asked for now.</summary>
    public List<Slot> All => [.. _slots.Keys];

    /// <summary>The timers for a plan: those it still has keep their times, and new ones are due at once.</summary>
    public void Replan(SubscriptionPlan plan, long now)
    {
        var wanted = Wanted(plan);
        foreach (var slot in _slots.Keys.Where(s => !wanted.Contains(s)).ToList())
            _slots.Remove(slot);
        foreach (var slot in wanted)
            _slots.TryAdd(slot, now);
    }

    /// <summary>Makes everything due at once.</summary>
    public void DueNow()
    {
        foreach (var slot in _slots.Keys.ToList())
            _slots[slot] = 0;
    }

    /// <summary>What's due at <paramref name="now"/> (or within 5 ms of it), each timer moved on to its next tick.</summary>
    public List<Slot> Due(long now)
    {
        var due = _slots.Where(s => s.Value <= now + 5).Select(s => s.Key).ToList();
        foreach (var slot in due)
            _slots[slot] = (now / Ms(slot.Every) + 1) * Ms(slot.Every);
        return due;
    }

    /// <summary>Whether nothing has been sent for a heartbeat, so an update goes even with nothing in it.</summary>
    public bool HeartbeatDue(long now, TimeSpan heartbeat) => now - _lastSent >= Ms(heartbeat);

    /// <summary>How long the budget held back an update going at <paramref name="now"/>, in microseconds, for the client's planner to lower its rates (Q210).</summary>
    public long HeldAt(long now) => _heldFrom is { } from && now > from ? (now - from) * 1000 : 0;

    /// <summary>Records an update of <paramref name="size"/> bytes sent just now, against the budget if there is one.</summary>
    public void Sent(int size, long? budget)
    {
        _lastSent = Environment.TickCount64;
        if (budget is { } bytesASecond)
        {
            // Bytes a second on average, with a second's worth allowed at once.
            _allowedAt = Math.Max(_allowedAt, _lastSent - 1000) + size * 1000.0 / bytesASecond;
        }
    }

    /// <summary>
    /// Waits until the next timer or heartbeat, or until <paramref name="input"/> or <paramref name="changed"/>
    /// completes, but never before the budget allows.
    /// </summary>
    public async Task WaitAsync(TimeSpan heartbeat, Task input, Task changed, CancellationToken cancellationToken)
    {
        _heldFrom = null;
        var now = Environment.TickCount64;
        var wake = _slots.Count == 0 ? now + Ms(heartbeat) : Math.Min(_slots.Values.Min(), _lastSent + Ms(heartbeat));
        var free = (long)Math.Ceiling(_allowedAt);
        if (free > wake)
        {
            _heldFrom = Math.Max(wake, now);
            wake = free;
        }
        if (wake <= now)
            return;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Graph changes and the client's own changes don't wait for the next tick, but do wait for the budget.
        if (free > now)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(free - now), timer.Token).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
            now = Environment.TickCount64;
        }
        if (wake > now && !cancellationToken.IsCancellationRequested)
            await Task.WhenAny(Task.Delay(TimeSpan.FromMilliseconds(wake - now), timer.Token), input, changed).ConfigureAwait(false);
        await timer.CancelAsync().ConfigureAwait(false);
    }

    private static long Ms(TimeSpan every) => (long)every.TotalMilliseconds;

    private static HashSet<Slot> Wanted(SubscriptionPlan plan)
    {
        var wanted = new HashSet<Slot>();
        if (plan.List is { } list)
            wanted.Add(new Slot(UpdateSection.List, "", list));
        if (plan.Host is { } hostRate)
            wanted.Add(new Slot(UpdateSection.Host, "", hostRate));
        if (plan.Runtime is { } runtime)
            wanted.Add(new Slot(UpdateSection.Runtime, "", runtime));
        foreach (var (entry, pipeline) in plan.Pipelines)
        {
            if (pipeline.Totals is { } totals)
                wanted.Add(new Slot(UpdateSection.Totals, entry, totals));
            foreach (var kind in SubscriptionPlan.PerModule)
            {
                foreach (var rate in pipeline.RatesOf(kind))
                    wanted.Add(new Slot(kind, entry, rate));
            }
        }
        return wanted;
    }
}
