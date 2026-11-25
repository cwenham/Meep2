using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meep2.Daemon.Subscriptions;

/// <summary>
/// One subscription on a <c>/ws</c> connection: what the client asked for, each at its own rate, sent as updates
/// carrying only what changed since the last one sent (Q202). See <see cref="SubscriptionPlan"/> for what it takes, and
/// <c>docs/Design/Chat/subscriptions.md</c> for what it sends.
/// </summary>
/// <remarks>
/// <para>
/// Each thing asked for has a timer at its rate, all on one grid, so things at 100 ms and 200 ms go out in the same
/// update. When one or more fall due, one update goes out with them all. Nothing is sent for what hasn't changed, and
/// an update with nothing in it isn't sent, unless nothing has been sent for <see cref="SubscriptionPlan.Heartbeat"/>.
/// A graph's changing is sent straight away.
/// </para>
/// <para>
/// The next update isn't made until the last has gone onto the socket, so a slow connection gets fewer, larger updates
/// rather than a growing queue; and with a budget, updates wait so that bytes sent stay under it on average. Either
/// way, nothing is lost: each update has everything that changed since the one before.
/// </para>
/// </remarks>
internal sealed class Subscription(SubscriptionPlan plan, ISubscriptionHost host)
{
    /// <summary>The order of each module's counts in an update.</summary>
    public static readonly string[] ModuleCounts =
        ["received", "handled", "emitted", "passed", "busy", "queued", "dropped", "filtered", "failed", "restarts", "expired", "refused", "denied"];

    /// <summary>The order of each link's counts.</summary>
    public static readonly string[] LinkCounts = ["carried", "errors"];

    /// <summary>The order of each module's bytes.</summary>
    public static readonly string[] ByteCounts = ["received", "sent"];

    /// <summary>The order of the counts for each place a module's messages arrive from or go to.</summary>
    public static readonly string[] PeerCounts = ["received", "sent", "bytesReceived", "bytesSent", "failed", "rejected"];

    /// <summary>The totals for a pipeline or the host, by name.</summary>
    public static readonly string[] TotalNames = [.. ModuleCounts, "sourced", "bytesReceived", "bytesSent"];

    // Where some counts are in ModuleCounts and TotalNames.
    internal const int Emitted = 2;
    internal const int Sourced = 13;
    internal const int BytesReceived = 14;
    internal const int BytesSent = 15;

    private readonly Lock _planning = new();
    private SubscriptionPlan _plan = plan;
    private TaskCompletionSource _input = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<(string Event, Action<UpdateWriter> Data)> _replies = new();
    private bool _resend;

    private readonly SubscriptionClock _clock = new();
    private readonly SentState _sent = new();

    private long _seq;
    private long _bytes;

    public bool Cbor => _plan.Cbor;

    /// <summary>Changes what it's for, by a merge patch. Things newly asked for go out in the next update, whole.</summary>
    /// <exception cref="DaemonException">The patch leaves a plan that isn't one; nothing changed.</exception>
    public void Change(JsonElement patch)
    {
        lock (_planning)
        {
            _plan = _plan.Change(patch);
            var json = _plan.Json.ToJsonString();
            _replies.Enqueue(("plan", writer => writer.Node(JsonNode.Parse(json))));
        }
        Wake();
    }

    /// <summary>Says a change was refused, as a <c>problem</c> event.</summary>
    public void Refused(string error)
    {
        lock (_planning)
        {
            _replies.Enqueue(("problem", writer =>
            {
                writer.StartMap(1);
                writer.Property("error", error);
                writer.EndMap();
            }));
        }
        Wake();
    }

    /// <summary>Sends everything asked for in the next update, whole, as after a gap in the sequence.</summary>
    public void Resend()
    {
        lock (_planning)
            _resend = true;
        Wake();
    }

    private void Wake()
    {
        TaskCompletionSource input;
        lock (_planning)
        {
            input = _input;
            _input = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        input.TrySetResult();
    }

    /// <summary>
    /// Sends <c>hello</c>, then updates until <paramref name="cancellationToken"/> is cancelled. <paramref name="send"/>
    /// sends one event and completes once it's on the socket, with its size in bytes.
    /// </summary>
    public async Task RunAsync(Func<string, Action<UpdateWriter>, Task<int>> send, CancellationToken cancellationToken)
    {
        var identity = host.Identity;
        string planned;
        lock (_planning)
            planned = _plan.Json.ToJsonString();
        _bytes += await send("hello", writer => Hello.Write(writer, identity, planned)).ConfigureAwait(false);
        _clock.Start();
        SubscriptionPlan? planUsed = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var (input, current, resend, replies) = TakeInput();
            foreach (var (name, data) in replies)
                _bytes += await send(name, data).ConfigureAwait(false);

            var now = Environment.TickCount64;
            if (!ReferenceEquals(current, planUsed))
            {
                Replan(current, now);
                planUsed = current;
            }
            if (resend)
                Forget();

            var due = _clock.Due(now);
            var changed = host.Changed; // taken before reading, so a change while reading wakes the next wait
            var update = Build(current, due);
            if (!update.IsEmpty || _clock.HeartbeatDue(now, current.Heartbeat))
                await SendAsync(send, update, _clock.HeldAt(now), current.Budget).ConfigureAwait(false);

            await _clock.WaitAsync(current.Heartbeat, input, changed, cancellationToken).ConfigureAwait(false);
        }
    }

    // What the client has asked for since it was last looked at: the task that completes at its next change, the plan,
    // whether to send everything whole, and the replies waiting to go.
    private (Task Input, SubscriptionPlan Plan, bool Resend, List<(string Event, Action<UpdateWriter> Data)> Replies) TakeInput()
    {
        lock (_planning)
        {
            var resend = _resend;
            _resend = false;
            List<(string Event, Action<UpdateWriter> Data)> replies = [.. _replies];
            _replies.Clear();
            return (_input.Task, _plan, resend, replies);
        }
    }

    private async Task SendAsync(Func<string, Action<UpdateWriter>, Task<int>> send, Update update, long held, long? budget)
    {
        var seq = ++_seq;
        var sentBefore = _bytes;
        var clock = DaemonClock.Now;
        var size = await send("update", writer => update.Write(writer, seq, clock, sentBefore, held)).ConfigureAwait(false);
        _bytes += size;
        _clock.Sent(size, budget);
    }

    /// <summary>
    /// Makes the update that would go out if everything asked for were due now, encoded as it would be sent, for
    /// measuring what an update costs. <paramref name="whole"/> sends everything as though for the first time.
    /// </summary>
    internal byte[] UpdateNow(bool whole)
    {
        var current = _plan;
        Replan(current, Environment.TickCount64);
        if (whole)
            Forget();
        var update = Build(current, _clock.All);
        var seq = ++_seq;
        void Write(UpdateWriter writer) => update.Write(writer, seq, DaemonClock.Now, _bytes, held: 0);
        return current.Cbor ? Envelope.Cbor("\"s\"", "update", Write) : Envelope.Json("\"s\"", "update", Write);
    }

    // The timers for a plan, and forgetting what was sent for what it no longer asks for.
    private void Replan(SubscriptionPlan current, long now)
    {
        _clock.Replan(current, now);
        _sent.Prune(current);
    }

    // Everything is sent whole in the next update, as though for the first time.
    private void Forget()
    {
        _sent.Forget();
        _clock.DueNow();
    }

    private Update Build(SubscriptionPlan current, List<Slot> due)
    {
        var update = new Update();
        var listed = host.Listed.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var reads = new ModuleReads();
        if (due.Any(s => s.What == UpdateSection.List))
            update.List = _sent.List(listed);
        if (due.Any(s => s.What == UpdateSection.Host))
            update.Host = _sent.Host(listed, reads);
        if (due.Any(s => s.What == UpdateSection.Runtime))
            update.Runtime = _sent.Runtime(host.Runtime);
        _sent.Pipelines(update, current, due, listed, reads);
        return update;
    }
}
