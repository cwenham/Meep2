namespace Meep2.Daemon.Subscriptions;

/// <summary>
/// What a subscription last sent, and finding what changed since, so each update carries only that (Q202).
/// </summary>
internal sealed class SentState
{
    private readonly Dictionary<string, string> _list = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _hostTotals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _states = new(StringComparer.Ordinal);
    private string? _listens;
    private string? _sends;
    private readonly Dictionary<string, RuntimeMetrics.Metric> _runtime = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PipelineSent> _pipelines = new(StringComparer.Ordinal);

    /// <summary>Forgets everything, so the next update sends it whole, as though for the first time.</summary>
    public void Forget()
    {
        _list.Clear();
        ForgetHost();
        _runtime.Clear();
        _pipelines.Clear();
    }

    /// <summary>Forgets what was sent for what <paramref name="plan"/> no longer asks for, so asking again sends it whole.</summary>
    public void Prune(SubscriptionPlan plan)
    {
        if (plan.List is null)
            _list.Clear();
        if (plan.Host is null)
            ForgetHost();
        if (plan.Runtime is null)
            _runtime.Clear();
        foreach (var (name, sent) in _pipelines.ToList())
        {
            if (plan.For(name) is { } pipeline)
                sent.Prune(pipeline);
            else
                _pipelines.Remove(name);
        }
    }

    /// <summary>Named values that changed since they were last sent, each by its name in <see cref="Subscription.TotalNames"/>.</summary>
    public static List<NamedCount> Changes(long[] now, Dictionary<string, long> sent)
    {
        var changes = new List<NamedCount>();
        for (var i = 0; i < now.Length; i++)
        {
            if (sent.TryGetValue(Subscription.TotalNames[i], out var before) && before == now[i])
                continue;
            sent[Subscription.TotalNames[i]] = now[i];
            changes.Add(new NamedCount(Subscription.TotalNames[i], now[i]));
        }
        return changes;
    }

    /// <summary>The pipelines whose summaries changed, with null for those gone; or null if none did.</summary>
    public Dictionary<string, PipelineSummary?>? List(IReadOnlyList<HostedPipeline> listed)
    {
        var changes = new Dictionary<string, PipelineSummary?>(StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hosted in listed)
        {
            present.Add(hosted.Name);
            var summary = hosted.Summary();
            var version = hosted.GraphVersion;
            var print = $"{summary.State}|{summary.Ended?.UtcTicks}|{summary.Reason}|{summary.Restarts}|{summary.Watching}|{version}|{summary.Started.UtcTicks}|{string.Join('\n', summary.Warnings)}|{summary.Path}";
            if (_list.TryGetValue(hosted.Name, out var before) && before == print)
                continue;
            _list[hosted.Name] = print;
            changes[hosted.Name] = summary with { GraphVersion = version };
        }
        foreach (var name in _list.Keys.Where(n => !present.Contains(n)).ToList())
        {
            _list.Remove(name);
            changes[name] = null;
        }
        return changes.Count > 0 ? changes : null;
    }

    /// <summary>What changed for the host: its totals, how many pipelines are in each state, its Listens and its Sends; or null if nothing did.</summary>
    public HostPart? Host(IReadOnlyList<HostedPipeline> listed, ModuleReads reads)
    {
        var totals = Changes(HostTotals(listed, reads), _hostTotals);
        var part = new HostPart
        {
            Totals = totals.Count > 0 ? totals : null,
            States = StateChanges(listed),
            Listens = ListensChanged(listed),
            Sends = SendsChanged(listed),
        };
        return part.IsEmpty ? null : part;
    }

    /// <summary>The runtime's metrics that changed, or null if none did.</summary>
    public Dictionary<string, RuntimeMetrics.Metric>? Runtime(RuntimeMetrics metrics)
    {
        var changes = new Dictionary<string, RuntimeMetrics.Metric>(StringComparer.Ordinal);
        foreach (var (name, metric) in metrics.Read())
        {
            if (_runtime.TryGetValue(name, out var before) && before.SameAs(metric))
                continue;
            _runtime[name] = metric;
            changes[name] = metric;
        }
        return changes.Count > 0 ? changes : null;
    }

    /// <summary>
    /// Adds to <paramref name="update"/> what changed in each pipeline <paramref name="plan"/> asks for, in the sections
    /// <paramref name="due"/>, with the pipelines that have gone and the graphs that changed.
    /// </summary>
    public void Pipelines(Update update, SubscriptionPlan plan, IReadOnlyList<Slot> due, IReadOnlyList<HostedPipeline> listed, ModuleReads reads)
    {
        Gone(update, listed);
        foreach (var hosted in listed)
        {
            if (plan.For(hosted.Name) is not { } wanted)
                continue;
            var entry = plan.Pipelines.ContainsKey(hosted.Name) ? hosted.Name : "*";
            var sent = Track(update, hosted);

            // Read from the pipeline while it runs, or as it was when it ended.
            if (hosted.Counts is not { } pipeline)
                continue; // still loading
            var read = new PipelineRead(update, hosted.Name, wanted, Every: default, pipeline, pipeline.Keys(), sent, reads);
            foreach (var slot in due)
            {
                if (slot.Entry == entry)
                    read.Read(slot);
            }
        }
    }

    private void ForgetHost()
    {
        _hostTotals.Clear();
        _states.Clear();
        _listens = null;
        _sends = null;
    }

    // Pipelines that have gone from the daemon, for those that had been sent.
    private void Gone(Update update, IReadOnlyList<HostedPipeline> listed)
    {
        var present = listed.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in _pipelines.Keys.Where(n => !present.Contains(n)).ToList())
        {
            update.Pipeline(name, gone: true);
            _pipelines.Remove(name);
        }
    }

    // What was sent for a pipeline, with its graph's version added to the update when that's new to the client.
    private PipelineSent Track(Update update, HostedPipeline hosted)
    {
        if (!_pipelines.TryGetValue(hosted.Name, out var sent) || !ReferenceEquals(sent.Hosted, hosted))
        {
            // New to this subscription, or started again under the same name: everything goes whole.
            _pipelines[hosted.Name] = sent = new PipelineSent(hosted) { Told = hosted.GraphNotice.Serial };
            if (hosted.GraphVersion is { } version)
                update.Pipeline(hosted.Name).Graph = (version, null);
        }
        else if (hosted.GraphNotice is { Notice: { } notice, Serial: var serial } && serial != sent.Told)
        {
            sent.Told = serial;
            update.Pipeline(hosted.Name).Graph = (notice.Version, notice.Reason);
        }
        return sent;
    }

    private static long[] HostTotals(IReadOnlyList<HostedPipeline> listed, ModuleReads reads)
    {
        var totals = new long[Subscription.TotalNames.Length];
        foreach (var hosted in listed)
        {
            if (hosted.Counts is not { } counts)
                continue;
            var sum = reads.TotalsOf(hosted.Name, counts);
            for (var i = 0; i < totals.Length; i++)
                totals[i] += sum[i];
        }
        return totals;
    }

    private List<NamedCount>? StateChanges(IReadOnlyList<HostedPipeline> listed)
    {
        var states = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var hosted in listed)
            states[hosted.State] = states.GetValueOrDefault(hosted.State) + 1;
        foreach (var gone in _states.Keys.Where(s => !states.ContainsKey(s)).ToList())
            states[gone] = 0;
        var changes = states
            .Where(s => !(_states.TryGetValue(s.Key, out var before) && before == s.Value))
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => new NamedCount(s.Key, s.Value))
            .ToList();
        foreach (var (state, count) in changes)
        {
            if (count == 0)
                _states.Remove(state);
            else
                _states[state] = count;
        }
        return changes.Count > 0 ? changes : null;
    }

    private List<ListenInfo>? ListensChanged(IReadOnlyList<HostedPipeline> listed)
    {
        var listens = new List<ListenInfo>();
        foreach (var hosted in listed)
        {
            if (hosted.Counts is null || hosted.Ended is not null || hosted.Pipeline is not { } pipeline || hosted.Live is not { } live)
                continue;
            var keys = live.ModuleKeys();
            foreach (var at in pipeline.GetListening())
                listens.Add(new ListenInfo(hosted.Name, keys.GetValueOrDefault(at.Module) ?? "", at.Module.ToString(), at.Address));
        }
        var print = string.Join('\n', listens.Select(l => $"{l.Pipeline}|{l.Module}|{l.Label}|{l.Address}"));
        if (print == _listens)
            return null;
        _listens = print;
        return listens;
    }

    private List<SendInfo>? SendsChanged(IReadOnlyList<HostedPipeline> listed)
    {
        var sends = DaemonApi.Sending(listed);
        var print = string.Join('\n', sends.Select(s => $"{s.Pipeline}|{s.Module}|{s.Label}|{s.Address}|{s.Computed}|{s.Endpoint}"));
        if (print == _sends)
            return null;
        _sends = print;
        return sends;
    }
}
