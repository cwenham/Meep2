namespace Meep2.Daemon.Subscriptions;

/// <summary>One update: what changed, by section, and how it's written.</summary>
internal sealed class Update
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

    /// <summary>
    /// Writes the update as its <paramref name="seq"/>uence number, the host's <paramref name="clock"/>, the
    /// <paramref name="bytes"/> sent before it, how long the budget <paramref name="held"/> it, and its sections.
    /// </summary>
    public void Write(UpdateWriter writer, long seq, long clock, long bytes, long held)
    {
        writer.StartMap(3 + (held > 0 ? 1 : 0) + (List is null ? 0 : 1) + (Host is null ? 0 : 1) + (Runtime is null ? 0 : 1) + (_pipelines.Count == 0 ? 0 : 1));
        writer.Property("seq", seq);
        writer.Property("clock", clock);
        writer.Property("bytes", bytes);
        if (held > 0)
            writer.Property("held", held);
        WriteList(writer);
        if (Host is { } host)
        {
            writer.Name(SubscriptionPlan.NameOf(UpdateSection.Host));
            host.Write(writer);
        }
        WriteRuntime(writer);
        WritePipelines(writer);
        writer.EndMap();
    }

    private void WriteList(UpdateWriter writer)
    {
        if (List is null)
            return;
        writer.Name(SubscriptionPlan.NameOf(UpdateSection.List));
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

    private void WriteRuntime(UpdateWriter writer)
    {
        if (Runtime is null)
            return;
        writer.Name(SubscriptionPlan.NameOf(UpdateSection.Runtime));
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

    private void WritePipelines(UpdateWriter writer)
    {
        if (_pipelines.Count == 0)
            return;
        writer.Name("pipelines");
        writer.StartMap(_pipelines.Count);
        foreach (var (name, part) in _pipelines)
        {
            writer.Name(name);
            part.Write(writer);
        }
        writer.EndMap();
    }

    private static void WriteSummary(UpdateWriter writer, PipelineSummary summary)
    {
        writer.StartMap(6 + (summary.Ended is null ? 0 : 1) + (summary.Reason is null ? 0 : 1) + (summary.GraphVersion is null ? 0 : 1) + (summary.Warnings.Count == 0 ? 0 : 1));
        writer.Property("path", summary.Path);
        writer.Property("state", summary.State);
        writer.Property("started", DaemonClock.Microseconds(summary.Started));
        if (summary.Ended is { } ended)
            writer.Property("ended", DaemonClock.Microseconds(ended));
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
