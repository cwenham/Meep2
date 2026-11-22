using System.Text.Json;
using System.Text.Json.Nodes;
using Meep2.Runtime;
using Meep2.SDK.Modules;

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
        ["received", "handled", "emitted", "passed", "busy", "queued", "dropped", "filtered", "failed", "restarts", "expired", "refused"];

    /// <summary>The order of each link's counts.</summary>
    public static readonly string[] LinkCounts = ["carried", "errors"];

    /// <summary>The order of each module's bytes.</summary>
    public static readonly string[] ByteCounts = ["received", "sent"];

    /// <summary>The order of the counts for each place a module's messages arrive from or go to.</summary>
    public static readonly string[] PeerCounts = ["received", "sent", "bytesReceived", "bytesSent", "failed", "rejected"];

    /// <summary>The totals for a pipeline or the host, by name.</summary>
    public static readonly string[] TotalNames = [.. ModuleCounts, "sourced", "bytesReceived", "bytesSent"];

    private readonly Lock _planning = new();
    private SubscriptionPlan _plan = plan;
    private TaskCompletionSource _input = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<(string Event, Action<UpdateWriter> Data)> _replies = new();
    private bool _resend;

    // Each thing asked for, by what, which pipeline entry and rate, with when it's next due, in TickCount64 ms.
    private readonly Dictionary<Slot, long> _slots = [];

    // What was last sent, to send only what changed.
    private readonly Dictionary<string, string> _listSent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _hostSent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _statesSent = new(StringComparer.Ordinal);
    private string? _listensSent;
    private string? _sendsSent;
    private readonly Dictionary<string, RuntimeMetrics.Metric> _runtimeSent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PipelineSent> _sent = new(StringComparer.Ordinal);

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
            _replies.Enqueue(("plan", writer => WriteNode(writer, JsonNode.Parse(json))));
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
        _bytes += await send("hello", writer => Hello(writer, identity, planned)).ConfigureAwait(false);
        var lastSent = Environment.TickCount64;
        var allowedAt = 0.0; // for the budget, in TickCount64 ms
        long? heldFrom = null; // when the next update would have gone, had the budget not held it back
        SubscriptionPlan? planUsed = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            Task input;
            SubscriptionPlan current;
            bool resend;
            List<(string Event, Action<UpdateWriter> Data)> replies;
            lock (_planning)
            {
                input = _input.Task;
                current = _plan;
                resend = _resend;
                _resend = false;
                replies = [.. _replies];
                _replies.Clear();
            }
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

            var due = _slots.Where(s => s.Value <= now + 5).Select(s => s.Key).ToList();
            foreach (var slot in due)
                _slots[slot] = (now / Ms(slot.Every) + 1) * Ms(slot.Every);
            var changed = host.Changed; // taken before reading, so a change while reading wakes the next wait
            var update = Build(current, due);
            var heartbeat = now - lastSent >= Ms(current.Heartbeat);
            if (!update.IsEmpty || heartbeat)
            {
                var seq = ++_seq;
                var sentBefore = _bytes;
                var clock = HostIdentity.Microseconds(DateTimeOffset.UtcNow);
                // How long the budget held it back, for the client's planner to lower its rates (Q210).
                var held = heldFrom is { } from && now > from ? (now - from) * 1000 : 0;
                var size = await send("update", writer => update.Write(writer, seq, clock, sentBefore, held)).ConfigureAwait(false);
                _bytes += size;
                lastSent = Environment.TickCount64;
                if (current.Budget is { } budget)
                {
                    // Bytes a second on average, with a second's worth allowed at once.
                    allowedAt = Math.Max(allowedAt, lastSent - 1000) + size * 1000.0 / budget;
                }
            }

            heldFrom = null;
            now = Environment.TickCount64;
            var wake = _slots.Count == 0 ? now + Ms(current.Heartbeat) : Math.Min(_slots.Values.Min(), lastSent + Ms(current.Heartbeat));
            var free = (long)Math.Ceiling(allowedAt); // when the budget lets the next update go
            if (free > wake)
            {
                heldFrom = Math.Max(wake, now);
                wake = free;
            }
            if (wake > now)
            {
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
        }
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
        var update = Build(current, [.. _slots.Keys]);
        var seq = ++_seq;
        void Write(UpdateWriter writer) => update.Write(writer, seq, HostIdentity.Microseconds(DateTimeOffset.UtcNow), _bytes, held: 0);
        return current.Cbor ? Envelope.Cbor("\"s\"", "update", Write) : Envelope.Json("\"s\"", "update", Write);
    }

    private static long Ms(TimeSpan every) => (long)every.TotalMilliseconds;

    // The timers for a plan: those it still has keep their times, and new ones are due at once.
    private void Replan(SubscriptionPlan current, long now)
    {
        var wanted = new HashSet<Slot>();
        if (current.List is { } list)
            wanted.Add(new Slot("list", "", list));
        if (current.Host is { } hostRate)
            wanted.Add(new Slot("host", "", hostRate));
        if (current.Runtime is { } runtime)
            wanted.Add(new Slot("runtime", "", runtime));
        foreach (var (entry, pipeline) in current.Pipelines)
        {
            if (pipeline.Totals is { } totals)
                wanted.Add(new Slot("totals", entry, totals));
            foreach (var kind in SubscriptionPlan.PerModule)
            {
                foreach (var rate in pipeline.RatesOf(kind))
                    wanted.Add(new Slot(kind, entry, rate));
            }
        }
        foreach (var slot in _slots.Keys.Where(s => !wanted.Contains(s)).ToList())
            _slots.Remove(slot);
        foreach (var slot in wanted)
            _slots.TryAdd(slot, now);

        // Forget what was sent for what's no longer asked for, so asking again sends it whole.
        if (current.List is null)
            _listSent.Clear();
        if (current.Host is null)
        {
            _hostSent.Clear();
            _statesSent.Clear();
            _listensSent = null;
            _sendsSent = null;
        }
        if (current.Runtime is null)
            _runtimeSent.Clear();
        foreach (var (name, sent) in _sent.ToList())
        {
            if (current.For(name) is not { } pipeline)
            {
                _sent.Remove(name);
                continue;
            }
            if (pipeline.Totals is null)
                sent.Totals.Clear();
            Prune(sent.Modules, "modules");
            Prune(sent.Links, "links");
            Prune(sent.Bytes, "bytes");
            Prune(sent.Timing, "timing");
            Prune(sent.Arrivals, "arrivals");
            Prune(sent.Destinations, "destinations");
            if (!pipeline.Wants("destinations"))
                sent.Peers.Clear();
            if (!pipeline.Wants("arrivals"))
                sent.Clients.Clear();

            void Prune<T>(Dictionary<string, T> values, string kind)
            {
                foreach (var key in values.Keys.Where(k => pipeline.RateOf(kind, k) is null).ToList())
                    values.Remove(key);
            }
        }
    }

    // Everything is sent whole in the next update, as though for the first time.
    private void Forget()
    {
        _listSent.Clear();
        _hostSent.Clear();
        _statesSent.Clear();
        _listensSent = null;
        _sendsSent = null;
        _runtimeSent.Clear();
        _sent.Clear();
        foreach (var slot in _slots.Keys.ToList())
            _slots[slot] = 0;
    }

    private Update Build(SubscriptionPlan current, List<Slot> due)
    {
        var update = new Update();
        var listed = host.Listed.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var byName = listed.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var sums = new Dictionary<string, long[]>(StringComparer.Ordinal); // totals worked out for this update
        var reads = new Reads();

        if (due.Any(s => s.What == "list"))
            update.List = List(listed);
        if (due.Any(s => s.What == "host"))
            update.Host = Host(listed, sums, reads);
        if (due.Any(s => s.What == "runtime"))
            update.Runtime = RuntimeChanges();

        // Pipelines that have gone from the daemon, for those that had been sent.
        foreach (var name in _sent.Keys.Where(n => !byName.ContainsKey(n)).ToList())
        {
            update.Pipeline(name, gone: true);
            _sent.Remove(name);
        }

        foreach (var hosted in listed)
        {
            if (current.For(hosted.Name) is not { } wanted)
                continue;
            var entry = current.Pipelines.ContainsKey(hosted.Name) ? hosted.Name : "*";
            if (!_sent.TryGetValue(hosted.Name, out var sent) || !ReferenceEquals(sent.Hosted, hosted))
            {
                // New to this subscription, or started again under the same name: everything goes whole.
                _sent[hosted.Name] = sent = new PipelineSent(hosted) { Told = hosted.GraphNotice.Serial };
                if (hosted.GraphVersion is { } version)
                    update.Pipeline(hosted.Name).Graph = (version, null);
            }
            else if (hosted.GraphNotice is { Notice: { } notice, Serial: var serial } && serial != sent.Told)
            {
                sent.Told = serial;
                update.Pipeline(hosted.Name).Graph = (notice.Version, notice.Reason);
            }

            // Read from the pipeline while it runs, or as it was when it ended.
            if (hosted.Counts is not { } pipeline)
                continue; // still loading
            var read = new PipelineRead(update, hosted.Name, wanted, Every: default, pipeline, pipeline.Keys(), sent, reads);

            foreach (var slot in due)
            {
                if (slot.Entry != entry)
                    continue;
                var atRate = read with { Every = slot.Every };
                switch (slot.What)
                {
                    case "totals":
                        if (Changes(TotalsOf(hosted, pipeline, sums, reads), sent.Totals) is { Count: > 0 } totals)
                            read.Part.Totals = totals;
                        break;
                    case "modules":
                        CountEach(atRate, "modules", sent.Modules, Keyed(atRate), static (module, _) => module.Counts);
                        break;
                    case "links":
                        CountEach(atRate, "links", sent.Links, pipeline.Links().Select(link => (link.Key, link)), static (link, _) => [link.Carried, link.Errors]);
                        break;
                    case "bytes":
                        // Only modules that talk to the network have any.
                        CountEach(atRate, "bytes", sent.Bytes, Keyed(atRate), static (module, sentBefore) =>
                            module.Traffic == default && !sentBefore ? null : [module.Traffic.Received, module.Traffic.Sent]);
                        break;
                    case "timing":
                        Timing(atRate);
                        break;
                    case "arrivals" or "destinations":
                        Places(atRate, slot.What);
                        break;
                }
            }
        }
        return update;
    }

    /// <summary>
    /// What the parts of an update read and write for one pipeline: the update, the pipeline's name, what's wanted of
    /// it and at which rate, its counts and its modules' keys, what was last sent, and the modules' counts read for
    /// this update.
    /// </summary>
    private sealed record PipelineRead(
        Update Update,
        string Name,
        PipelinePlan Wanted,
        TimeSpan Every,
        PipelineCounts Pipeline,
        IReadOnlyDictionary<ModuleInfo, string> Keys,
        PipelineSent Sent,
        Reads Reads)
    {
        /// <summary>The pipeline's part of the update.</summary>
        public PipelinePart Part => Update.Pipeline(Name);

        /// <summary>Whether a module's or link's <paramref name="kind"/> is wanted at this rate.</summary>
        public bool AtRate(string kind, string key) => Wanted.RateOf(kind, key) == Every;
    }

    // Each module with a key, and its counts read for this update.
    private static IEnumerable<(string Key, ModuleRead Module)> Keyed(PipelineRead read)
    {
        foreach (var module in read.Reads.Of(read.Pipeline))
        {
            if (read.Keys.TryGetValue(module.Module, out var key))
                yield return (key, module);
        }
    }

    // The counts of each module or link wanted at this rate, if they changed, and those sent before that have gone.
    // countsOf is given whether the key's counts were sent before, and returns null to send nothing for it yet.
    private static void CountEach<T>(PipelineRead read, string kind, Dictionary<string, long[]> sent, IEnumerable<(string Key, T Item)> items, Func<T, bool, long[]?> countsOf)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, item) in items)
        {
            if (!read.AtRate(kind, key))
                continue;
            seen.Add(key);
            if (countsOf(item, sent.ContainsKey(key)) is { } counts)
                Counts(read, kind, key, counts, sent);
        }
        Gone(read, kind, sent, seen);
    }

    private static void Timing(PipelineRead read)
    {
        var sent = read.Sent.Timing;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, module) in Keyed(read))
        {
            if (!read.AtRate("timing", key))
                continue;
            seen.Add(key);
            // The busy time moves whenever the histogram does, and costs far less to read than the histogram.
            var busy = module.Busy.Ticks;
            if (sent.TryGetValue(key, out var before) ? before == busy : busy == 0 && module.Source)
                continue; // nothing new, or a source, which handles nothing
            var times = read.Pipeline.GetHandlingTimes(module.Module);
            sent[key] = busy;
            (read.Part.Timing ??= []).Add((key, times));
        }
        foreach (var key in sent.Keys.Where(k => !seen.Contains(k) && read.AtRate("timing", k)).ToList())
        {
            sent.Remove(key);
            (read.Part.Timing ??= []).Add((key, null));
        }
    }

    // A module's counts by place: where its messages arrive from, or where they go. Each place's counts go as an array,
    // as a module's do. A place past the module's bound is counted under * (or meep://*). A place folded there for
    // being quiet goes (null), and is named in the pipeline's folded, so a client moves its counts into *'s rather than
    // seeing *'s rate jump (Q215). For arrivals, the pipeline's clients names clients outside Meep, by address, once a
    // name is found (Q220); for destinations, its peers says which are Meep hosts, by ID.
    private static void Places(PipelineRead read, string kind)
    {
        var arrivals = kind == "arrivals";
        var sent = read.Sent;
        var record = arrivals ? sent.Arrivals : sent.Destinations;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (module, places) in read.Pipeline.ReadPeers(arrivals))
        {
            if (!read.Keys.TryGetValue(module, out var key) || !read.AtRate(kind, key))
                continue;
            seen.Add(key);
            if (!record.TryGetValue(key, out var before))
                record[key] = before = new Dictionary<string, long[]>(StringComparer.Ordinal);
            var now = new Dictionary<string, long[]>(StringComparer.Ordinal);
            foreach (var place in places)
            {
                long[] counts = [place.Received, place.Sent, place.BytesReceived, place.BytesSent, place.Failed, place.Rejected];
                // A place counted with nothing yet, as a WebSocket's client before its first message, isn't shown.
                if (!before.ContainsKey(place.Place) && counts.All(c => c == 0))
                    continue;
                now[place.Place] = counts;
                if (arrivals)
                    Client(read, place.Place);
                else
                    Peer(read, place.Place, place.Meep);
            }
            List<(string Place, long[]? Now, long[]? Before)>? changes = null;
            foreach (var (place, counts) in now)
            {
                var last = before.GetValueOrDefault(place);
                if (last is not null && counts.AsSpan().SequenceEqual(last))
                    continue;
                if (last is not null && counts.Where((count, i) => count < last[i]).Any())
                {
                    // Folded and back again since the last update: it starts again from what it has now.
                    read.Part.Fold(key, place);
                    last = null;
                }
                (changes ??= []).Add((place, counts, last));
                before[place] = counts;
            }
            foreach (var place in before.Keys.Where(p => !now.ContainsKey(p)).ToList())
            {
                // Places go only by being folded into * (or meep://*).
                before.Remove(place);
                (changes ??= []).Add((place, null, null));
                read.Part.Fold(key, place);
            }
            if (changes is not null)
                read.Part.AddPlaces(kind, key, changes);
        }
        foreach (var key in record.Keys.Where(k => !seen.Contains(k) && read.AtRate(kind, k)).ToList())
        {
            record.Remove(key);
            read.Part.AddPlaces(kind, key, null);
        }
    }

    // Names a client outside Meep by its address, when a name is first found for it or changes.
    private static void Client(PipelineRead read, string place)
    {
        var sent = read.Sent;
        if (ClientNames.NameOf(place) is not { } found || (sent.Clients.TryGetValue(place, out var told) && told == found))
            return;
        sent.Clients[place] = found;
        (read.Part.Clients ??= []).Add((place, found));
    }

    // Says which Meep host a destination is, when that's new or has changed.
    private static void Peer(PipelineRead read, string endpoint, string? id)
    {
        var sent = read.Sent;
        if (sent.Peers.TryGetValue(endpoint, out var told) ? told == id : id is null)
            return;
        if (id is null)
            sent.Peers.Remove(endpoint);
        else
            sent.Peers[endpoint] = id;
        (read.Part.Peers ??= []).Add((endpoint, id));
    }

    // A module's or link's counts, if any changed since they were last sent.
    private static void Counts(PipelineRead read, string kind, string key, long[] now, Dictionary<string, long[]> sent)
    {
        if (sent.TryGetValue(key, out var before))
        {
            if (now.AsSpan().SequenceEqual(before))
                return;
            read.Part.Add(kind, key, now, before);
        }
        else
        {
            read.Part.Add(kind, key, now, null);
        }
        sent[key] = now;
    }

    // Modules or links that were sent at this rate and aren't there now, as when an edit removed them.
    private static void Gone(PipelineRead read, string kind, Dictionary<string, long[]> sent, HashSet<string> seen)
    {
        foreach (var key in sent.Keys.Where(k => !seen.Contains(k) && read.AtRate(kind, k)).ToList())
        {
            sent.Remove(key);
            read.Part.Add(kind, key, null, null);
        }
    }

    // A pipeline's counts added up over its modules, with what its sources emitted and its bytes.
    private static long[] TotalsOf(HostedPipeline hosted, PipelineCounts pipeline, Dictionary<string, long[]> sums, Reads reads)
    {
        if (sums.TryGetValue(hosted.Name, out var made))
            return made;
        var totals = new long[TotalNames.Length];
        foreach (var read in reads.Of(pipeline))
        {
            for (var i = 0; i < read.Counts.Length; i++)
                totals[i] += read.Counts[i];
            if (read.Source)
                totals[12] += read.Counts[2];
            totals[13] += read.Traffic.Received;
            totals[14] += read.Traffic.Sent;
        }
        return sums[hosted.Name] = totals;
    }

    /// <summary>One module's counts, read once for an update, so its totals and its modules agree.</summary>
    private readonly record struct ModuleRead(ModuleInfo Module, long[] Counts, ModuleTraffic Traffic, TimeSpan Busy, bool Source);

    /// <summary>The modules' counts read for one update, each pipeline's once.</summary>
    private sealed class Reads
    {
        private readonly Dictionary<PipelineCounts, ModuleRead[]> _read = new(ReferenceEqualityComparer.Instance);

        public ModuleRead[] Of(PipelineCounts pipeline)
        {
            if (_read.TryGetValue(pipeline, out var read))
                return read;
            var readings = pipeline.ReadModules();
            read = new ModuleRead[readings.Count];
            for (var i = 0; i < read.Length; i++)
            {
                var (module, stats, traffic, busy, source) = readings[i];
                read[i] = new ModuleRead(
                    module,
                    [
                        stats.Received, stats.Handled, stats.Emitted, stats.Passed, (long)busy.TotalMicroseconds, stats.Queued,
                        stats.Dropped, stats.Filtered, stats.Failed, stats.Restarts, stats.Expired, stats.Refused,
                    ],
                    traffic,
                    busy,
                    source);
            }
            return _read[pipeline] = read;
        }
    }

    // Named values that changed since they were last sent.
    private static List<(string Name, long Value)> Changes(long[] now, Dictionary<string, long> sent)
    {
        var changes = new List<(string, long)>();
        for (var i = 0; i < now.Length; i++)
        {
            if (sent.TryGetValue(TotalNames[i], out var before) && before == now[i])
                continue;
            sent[TotalNames[i]] = now[i];
            changes.Add((TotalNames[i], now[i]));
        }
        return changes;
    }

    private Dictionary<string, PipelineSummary?>? List(List<HostedPipeline> listed)
    {
        var changes = new Dictionary<string, PipelineSummary?>(StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hosted in listed)
        {
            present.Add(hosted.Name);
            var summary = hosted.Summary();
            var version = hosted.GraphVersion;
            var print = $"{summary.State}|{summary.Ended?.UtcTicks}|{summary.Reason}|{summary.Restarts}|{summary.Watching}|{version}|{summary.Started.UtcTicks}|{string.Join('\n', summary.Warnings)}|{summary.Path}";
            if (_listSent.TryGetValue(hosted.Name, out var before) && before == print)
                continue;
            _listSent[hosted.Name] = print;
            changes[hosted.Name] = summary with { GraphVersion = version };
        }
        foreach (var name in _listSent.Keys.Where(n => !present.Contains(n)).ToList())
        {
            _listSent.Remove(name);
            changes[name] = null;
        }
        return changes.Count > 0 ? changes : null;
    }

    private HostPart? Host(List<HostedPipeline> listed, Dictionary<string, long[]> sums, Reads reads)
    {
        var totals = new long[TotalNames.Length];
        var states = new Dictionary<string, long>(StringComparer.Ordinal);
        var listens = new List<(string Pipeline, string Module, string Label, string Address)>();
        foreach (var hosted in listed)
        {
            states[hosted.State] = states.GetValueOrDefault(hosted.State) + 1;
            if (hosted.Counts is not { } counts)
                continue;
            var sum = TotalsOf(hosted, counts, sums, reads);
            for (var i = 0; i < totals.Length; i++)
                totals[i] += sum[i];
            if (hosted.Ended is null && hosted.Pipeline is { } pipeline && hosted.Live is { } live)
            {
                var keys = live.ModuleKeys();
                foreach (var at in pipeline.GetListening())
                    listens.Add((hosted.Name, keys.GetValueOrDefault(at.Module) ?? "", at.Module.ToString(), at.Address));
            }
        }
        var part = new HostPart();
        if (Changes(totals, _hostSent) is { Count: > 0 } changed)
            part.Totals = changed;
        foreach (var gone in _statesSent.Keys.Where(s => !states.ContainsKey(s)).ToList())
            states[gone] = 0;
        var stateChanges = states.Where(s => !(_statesSent.TryGetValue(s.Key, out var before) && before == s.Value)).OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
        foreach (var (state, count) in stateChanges)
        {
            if (count == 0)
                _statesSent.Remove(state);
            else
                _statesSent[state] = count;
        }
        if (stateChanges.Count > 0)
            part.States = stateChanges;
        var print = string.Join('\n', listens.Select(l => $"{l.Pipeline}|{l.Module}|{l.Label}|{l.Address}"));
        if (print != _listensSent)
        {
            _listensSent = print;
            part.Listens = listens;
        }
        var sends = MeepDaemon.Sending(listed);
        var sendsPrint = string.Join('\n', sends.Select(s => $"{s.Pipeline}|{s.Module}|{s.Label}|{s.Address}|{s.Computed}|{s.Endpoint}"));
        if (sendsPrint != _sendsSent)
        {
            _sendsSent = sendsPrint;
            part.Sends = sends;
        }
        return part.Totals is null && part.States is null && part.Listens is null && part.Sends is null ? null : part;
    }

    private Dictionary<string, RuntimeMetrics.Metric>? RuntimeChanges()
    {
        var changes = new Dictionary<string, RuntimeMetrics.Metric>(StringComparer.Ordinal);
        foreach (var (name, metric) in host.Runtime.Read())
        {
            if (_runtimeSent.TryGetValue(name, out var before) && before.SameAs(metric))
                continue;
            _runtimeSent[name] = metric;
            changes[name] = metric;
        }
        return changes.Count > 0 ? changes : null;
    }

    private static void Hello(UpdateWriter writer, HostInfo identity, string plan)
    {
        writer.StartMap(5);
        writer.Name("host");
        WriteHost(writer, identity);
        writer.Property("clock", HostIdentity.Microseconds(DateTimeOffset.UtcNow));
        writer.Name("counts");
        writer.StartMap(6);
        Names(writer, "modules", ModuleCounts);
        Names(writer, "links", LinkCounts);
        Names(writer, "bytes", ByteCounts);
        Names(writer, "arrivals", PeerCounts);
        Names(writer, "destinations", PeerCounts);
        Names(writer, "totals", TotalNames);
        writer.EndMap();
        writer.Name("timing");
        writer.StartMap(2);
        writer.Property("scale", (long)HandlingTimes.Empty.Scale);
        writer.Property("unit", "s");
        writer.EndMap();
        writer.Name("plan");
        WriteNode(writer, JsonNode.Parse(plan));
        writer.EndMap();

        static void Names(UpdateWriter writer, string name, string[] names)
        {
            writer.Name(name);
            writer.StartArray(names.Length);
            foreach (var each in names)
                writer.String(each);
            writer.EndArray();
        }
    }

    internal static void WriteHost(UpdateWriter writer, HostInfo identity)
    {
        writer.StartMap(5);
        writer.Property("id", identity.Id);
        writer.Name("names");
        writer.StartArray(identity.Names.Count);
        foreach (var name in identity.Names)
            writer.String(name);
        writer.EndArray();
        writer.Name("addresses");
        writer.StartArray(identity.Addresses.Count);
        foreach (var address in identity.Addresses)
            writer.String(address);
        writer.EndArray();
        writer.Property("version", identity.Version);
        writer.Property("pid", identity.ProcessId);
        writer.EndMap();
    }

    private static void WriteNode(UpdateWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.Null();
                break;
            case JsonObject map:
                writer.StartMap(map.Count);
                foreach (var (name, value) in map)
                {
                    writer.Name(name);
                    WriteNode(writer, value);
                }
                writer.EndMap();
                break;
            case JsonArray array:
                writer.StartArray(array.Count);
                foreach (var value in array)
                    WriteNode(writer, value);
                writer.EndArray();
                break;
            default:
                switch (node.GetValueKind())
                {
                    case JsonValueKind.String:
                        writer.String(node.GetValue<string>());
                        break;
                    case JsonValueKind.True or JsonValueKind.False:
                        writer.Bool(node.GetValue<bool>());
                        break;
                    case JsonValueKind.Number:
                        if (node.AsValue().TryGetValue<long>(out var whole))
                            writer.Number(whole);
                        else
                            writer.Number(node.GetValue<double>());
                        break;
                    default:
                        writer.Null();
                        break;
                }
                break;
        }
    }

    /// <summary>One thing asked for: what, for which pipeline entry (or none), at what rate.</summary>
    private readonly record struct Slot(string What, string Entry, TimeSpan Every);

    /// <summary>What was last sent for one pipeline.</summary>
    private sealed class PipelineSent(HostedPipeline hosted)
    {
        public HostedPipeline Hosted { get; } = hosted;

        public int Told { get; set; }

        public Dictionary<string, long> Totals { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, long[]> Modules { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, long[]> Links { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, long[]> Bytes { get; } = new(StringComparer.Ordinal);

        // The busy time last sent with each module's histogram, which changes whenever it does.
        public Dictionary<string, long> Timing { get; } = new(StringComparer.Ordinal);

        // Each module's counts by place, as last sent.
        public Dictionary<string, Dictionary<string, long[]>> Arrivals { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Dictionary<string, long[]>> Destinations { get; } = new(StringComparer.Ordinal);

        // The Meep host at each destination, as last sent.
        public Dictionary<string, string> Peers { get; } = new(StringComparer.Ordinal);

        // The name of each client outside Meep, by address, as last sent.
        public Dictionary<string, string> Clients { get; } = new(StringComparer.Ordinal);
    }

    private sealed class HostPart
    {
        public List<(string Name, long Value)>? Totals { get; set; }

        public List<KeyValuePair<string, long>>? States { get; set; }

        public List<(string Pipeline, string Module, string Label, string Address)>? Listens { get; set; }

        public List<SendInfo>? Sends { get; set; }
    }

    private sealed class PipelinePart
    {
        public bool Gone { get; set; }

        public (int Version, string? Reason)? Graph { get; set; }

        public List<(string Name, long Value)>? Totals { get; set; }

        public Dictionary<string, List<(string Key, long[]? Now, long[]? Before)>> Counts { get; } = new(StringComparer.Ordinal);

        public List<(string Key, HandlingTimes? Times)>? Timing { get; set; }

        public Dictionary<string, List<(string Key, List<(string Place, long[]? Now, long[]? Before)>? Places)>> Places { get; } = new(StringComparer.Ordinal);

        public List<(string Endpoint, string? Id)>? Peers { get; set; }

        public List<(string Address, string Name)>? Clients { get; set; }

        public Dictionary<string, List<string>>? Folded { get; set; }

        public void Fold(string key, string place)
        {
            Folded ??= new(StringComparer.Ordinal);
            if (!Folded.TryGetValue(key, out var places))
                Folded[key] = places = [];
            places.Add(place);
        }

        public void AddPlaces(string kind, string key, List<(string Place, long[]? Now, long[]? Before)>? places)
        {
            if (!Places.TryGetValue(kind, out var list))
                Places[kind] = list = [];
            list.Add((key, places));
        }

        public void Add(string kind, string key, long[]? now, long[]? before)
        {
            if (!Counts.TryGetValue(kind, out var list))
                Counts[kind] = list = [];
            list.Add((key, now, before));
        }

        public int Size =>
            (Graph is null ? 0 : 1) + (Totals is null ? 0 : 1) + Counts.Count + (Timing is null ? 0 : 1) + Places.Count + (Peers is null ? 0 : 1) + (Clients is null ? 0 : 1) + (Folded is null ? 0 : 1);
    }

    /// <summary>One update: what changed, by section.</summary>
    private sealed class Update
    {
        private readonly SortedDictionary<string, PipelinePart> _pipelines = new(StringComparer.Ordinal);

        public Dictionary<string, PipelineSummary?>? List { get; set; }

        public HostPart? Host { get; set; }

        public Dictionary<string, RuntimeMetrics.Metric>? Runtime { get; set; }

        public bool IsEmpty => List is null && Host is null && Runtime is null && _pipelines.Count == 0;

        public PipelinePart Pipeline(string name, bool gone = false)
        {
            if (!_pipelines.TryGetValue(name, out var part))
                _pipelines[name] = part = new PipelinePart();
            part.Gone |= gone;
            return part;
        }

        public void Write(UpdateWriter writer, long seq, long clock, long bytes, long held)
        {
            writer.StartMap(3 + (held > 0 ? 1 : 0) + (List is null ? 0 : 1) + (Host is null ? 0 : 1) + (Runtime is null ? 0 : 1) + (_pipelines.Count == 0 ? 0 : 1));
            writer.Property("seq", seq);
            writer.Property("clock", clock);
            writer.Property("bytes", bytes);
            if (held > 0)
                writer.Property("held", held);
            if (List is not null)
            {
                writer.Name("list");
                writer.StartMap(List.Count);
                foreach (var (name, summary) in List.OrderBy(l => l.Key, StringComparer.Ordinal))
                {
                    writer.Name(name);
                    if (summary is null)
                        writer.Null();
                    else
                        WriteSummary(writer, summary);
                }
                writer.EndMap();
            }
            if (Host is { } hostPart)
            {
                writer.Name("host");
                writer.StartMap((hostPart.Totals is null ? 0 : 1) + (hostPart.States is null ? 0 : 1) + (hostPart.Listens is null ? 0 : 1) + (hostPart.Sends is null ? 0 : 1));
                if (hostPart.Totals is { } totals)
                {
                    writer.Name("totals");
                    WriteNamed(writer, totals);
                }
                if (hostPart.States is { } states)
                {
                    writer.Name("states");
                    writer.StartMap(states.Count);
                    foreach (var (state, count) in states)
                        writer.Property(state, count);
                    writer.EndMap();
                }
                if (hostPart.Listens is { } listens)
                {
                    writer.Name("listens");
                    writer.StartArray(listens.Count);
                    foreach (var (pipeline, module, label, address) in listens)
                    {
                        writer.StartMap(4);
                        writer.Property("pipeline", pipeline);
                        writer.Property("module", module);
                        writer.Property("label", label);
                        writer.Property("address", address);
                        writer.EndMap();
                    }
                    writer.EndArray();
                }
                if (hostPart.Sends is { } sends)
                {
                    writer.Name("sends");
                    writer.StartArray(sends.Count);
                    foreach (var send in sends)
                    {
                        writer.StartMap(4 + (send.Computed ? 1 : 0) + (send.Endpoint is null ? 0 : 1));
                        writer.Property("pipeline", send.Pipeline);
                        writer.Property("module", send.Module);
                        writer.Property("label", send.Label);
                        writer.Property("address", send.Address);
                        if (send.Computed)
                            writer.Property("computed", true);
                        if (send.Endpoint is { } endpoint)
                            writer.Property("endpoint", endpoint);
                        writer.EndMap();
                    }
                    writer.EndArray();
                }
                writer.EndMap();
            }
            if (Runtime is not null)
            {
                writer.Name("runtime");
                writer.StartMap(Runtime.Count);
                foreach (var (name, metric) in Runtime.OrderBy(r => r.Key, StringComparer.Ordinal))
                {
                    writer.Name(name);
                    if (metric.Value is { } value)
                    {
                        writer.Number(value);
                        continue;
                    }
                    writer.StartMap(metric.Values!.Count);
                    foreach (var (key, each) in metric.Values)
                        writer.Property(key, each);
                    writer.EndMap();
                }
                writer.EndMap();
            }
            if (_pipelines.Count > 0)
            {
                writer.Name("pipelines");
                writer.StartMap(_pipelines.Count);
                foreach (var (name, part) in _pipelines)
                {
                    writer.Name(name);
                    if (part.Gone)
                    {
                        writer.Null();
                        continue;
                    }
                    writer.StartMap(part.Size);
                    if (part.Graph is { } graph)
                    {
                        writer.Name("graph");
                        writer.StartMap(graph.Reason is null ? 1 : 2);
                        writer.Property("version", graph.Version);
                        if (graph.Reason is { } reason)
                            writer.Property("reason", reason);
                        writer.EndMap();
                    }
                    if (part.Totals is { } totals)
                    {
                        writer.Name("totals");
                        WriteNamed(writer, totals);
                    }
                    foreach (var (kind, counts) in part.Counts)
                    {
                        writer.Name(kind);
                        WriteChanges(writer, counts);
                    }
                    foreach (var (kind, modules) in part.Places)
                    {
                        writer.Name(kind);
                        writer.StartMap(modules.Count);
                        foreach (var (key, places) in modules)
                        {
                            writer.Name(key);
                            if (places is null)
                            {
                                writer.Null();
                                continue;
                            }
                            WriteChanges(writer, places);
                        }
                        writer.EndMap();
                    }
                    if (part.Folded is { } folded)
                    {
                        writer.Name("folded");
                        writer.StartMap(folded.Count);
                        foreach (var (key, places) in folded)
                        {
                            writer.Name(key);
                            writer.StartArray(places.Count);
                            foreach (var place in places)
                                writer.String(place);
                            writer.EndArray();
                        }
                        writer.EndMap();
                    }
                    if (part.Clients is { } clients)
                    {
                        writer.Name("clients");
                        writer.StartMap(clients.Count);
                        foreach (var (address, client) in clients)
                            writer.Property(address, client);
                        writer.EndMap();
                    }
                    if (part.Peers is { } peers)
                    {
                        writer.Name("peers");
                        writer.StartMap(peers.Count);
                        foreach (var (endpoint, id) in peers)
                        {
                            writer.Name(endpoint);
                            if (id is null)
                                writer.Null();
                            else
                                writer.String(id);
                        }
                        writer.EndMap();
                    }
                    if (part.Timing is { } timing)
                    {
                        writer.Name("timing");
                        writer.StartMap(timing.Count);
                        foreach (var (key, times) in timing)
                        {
                            writer.Name(key);
                            if (times is null)
                                writer.Null();
                            else
                                WriteTimes(writer, times);
                        }
                        writer.EndMap();
                    }
                    writer.EndMap();
                }
                writer.EndMap();
            }
            writer.EndMap();
        }

        // Counts by key that changed: the whole array for a key new to the client, only what changed otherwise, and
        // null for a key that's gone.
        private static void WriteChanges(UpdateWriter writer, List<(string Key, long[]? Now, long[]? Before)> changes)
        {
            writer.StartMap(changes.Count);
            foreach (var (key, now, before) in changes)
            {
                writer.Name(key);
                if (now is null)
                    writer.Null();
                else
                    writer.Changed(now, before ?? now, all: before is null);
            }
            writer.EndMap();
        }

        private static void WriteNamed(UpdateWriter writer, List<(string Name, long Value)> values)
        {
            writer.StartMap(values.Count);
            foreach (var (name, value) in values)
                writer.Property(name, value);
            writer.EndMap();
        }

        private static void WriteTimes(UpdateWriter writer, HandlingTimes times)
        {
            writer.StartMap(7);
            writer.Property("count", times.Count);
            writer.Property("sum", times.Sum);
            writer.Property("min", times.Min);
            writer.Property("max", times.Max);
            writer.Property("zero", times.Zero);
            writer.Property("offset", (long)times.Offset);
            writer.Name("buckets");
            writer.StartArray(times.Buckets.Count);
            foreach (var count in times.Buckets)
                writer.Number(count);
            writer.EndArray();
            writer.EndMap();
        }

        private static void WriteSummary(UpdateWriter writer, PipelineSummary summary)
        {
            writer.StartMap(6 + (summary.Ended is null ? 0 : 1) + (summary.Reason is null ? 0 : 1) + (summary.GraphVersion is null ? 0 : 1) + (summary.Warnings.Count == 0 ? 0 : 1));
            writer.Property("path", summary.Path);
            writer.Property("state", summary.State);
            writer.Property("started", HostIdentity.Microseconds(summary.Started));
            if (summary.Ended is { } ended)
                writer.Property("ended", HostIdentity.Microseconds(ended));
            if (summary.Reason is { } reason)
                writer.Property("reason", reason);
            writer.Property("restarts", (long)summary.Restarts);
            writer.Property("watching", summary.Watching);
            if (summary.GraphVersion is { } version)
                writer.Property("graphVersion", (long)version);
            if (summary.Warnings.Count > 0)
            {
                writer.Name("warnings");
                writer.StartArray(summary.Warnings.Count);
                foreach (var warning in summary.Warnings)
                    writer.String(warning);
                writer.EndArray();
            }
            writer.Property("name", summary.Name);
            writer.EndMap();
        }
    }
}
