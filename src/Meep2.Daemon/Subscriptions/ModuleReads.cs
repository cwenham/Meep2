namespace Meep2.Daemon.Subscriptions;

/// <summary>
/// The modules' counts read for one update, each pipeline's once, and each pipeline's totals added up from them once,
/// so the host's totals, a pipeline's and its modules' all agree.
/// </summary>
internal sealed class ModuleReads
{
    private readonly Dictionary<PipelineCounts, ModuleRead[]> _read = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, long[]> _totals = new(StringComparer.Ordinal);

    /// <summary>The counts of each of a pipeline's modules.</summary>
    public ModuleRead[] Of(PipelineCounts pipeline)
    {
        if (_read.TryGetValue(pipeline, out var read))
            return read;
        var readings = pipeline.ReadModules();
        read = new ModuleRead[readings.Count];
        for (var i = 0; i < read.Length; i++)
            read[i] = ModuleRead.Of(readings[i]);
        return _read[pipeline] = read;
    }

    /// <summary>
    /// A pipeline's counts added up over its modules, with what its sources emitted and its bytes, in the order of
    /// <see cref="Subscription.TotalNames"/>.
    /// </summary>
    public long[] TotalsOf(string name, PipelineCounts pipeline)
    {
        if (_totals.TryGetValue(name, out var made))
            return made;
        var totals = new long[Subscription.TotalNames.Length];
        foreach (var read in Of(pipeline))
        {
            for (var i = 0; i < read.Counts.Length; i++)
                totals[i] += read.Counts[i];
            if (read.Source)
                totals[Subscription.Sourced] += read.Counts[Subscription.Emitted];
            totals[Subscription.BytesReceived] += read.Traffic.Received;
            totals[Subscription.BytesSent] += read.Traffic.Sent;
        }
        return _totals[name] = totals;
    }
}
