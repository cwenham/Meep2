namespace Meep2.Daemon.Subscriptions;

/// <summary>What a subscription last sent for one pipeline.</summary>
internal sealed class PipelineSent(HostedPipeline hosted)
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

    /// <summary>Forgets what was sent for what <paramref name="plan"/> no longer asks for, so asking again sends it whole.</summary>
    public void Prune(PipelinePlan plan)
    {
        if (plan.Totals is null)
            Totals.Clear();
        Prune(Modules, UpdateSection.Modules);
        Prune(Links, UpdateSection.Links);
        Prune(Bytes, UpdateSection.Bytes);
        Prune(Timing, UpdateSection.Timing);
        Prune(Arrivals, UpdateSection.Arrivals);
        Prune(Destinations, UpdateSection.Destinations);
        if (!plan.Wants(UpdateSection.Destinations))
            Peers.Clear();
        if (!plan.Wants(UpdateSection.Arrivals))
            Clients.Clear();

        void Prune<T>(Dictionary<string, T> values, UpdateSection kind)
        {
            foreach (var key in values.Keys.Where(k => plan.RateOf(kind, k) is null).ToList())
                values.Remove(key);
        }
    }
}
