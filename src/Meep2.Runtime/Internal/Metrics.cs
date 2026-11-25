using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Meep2.Runtime.Internal;

/// <summary>
/// Meep's OpenTelemetry metrics, through .NET's own <see cref="Meter"/> (Q43, Q64). With nothing listening they cost
/// nothing: most of them are read from the counts each module keeps anyway.
/// </summary>
internal static class Metrics
{
    public static readonly Meter Meter = new(MeepTelemetry.Name, typeof(Metrics).Assembly.GetName().Version?.ToString());

    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "meep.module.duration", "s", "How long a module took to handle a message.");

    static Metrics()
    {
        Meter.CreateObservableCounter("meep.module.messages", ObserveCounts, "{message}",
            "Messages through each module, by what happened to them: received, handled, emitted, passed, dropped, filtered, expired, refused or denied.");
        Meter.CreateObservableCounter("meep.module.failures", ObserveFailures, "{failure}",
            "Times each module threw, and times it was restarted.");
        Meter.CreateObservableUpDownCounter("meep.module.queued", ObserveQueues, "{message}",
            "Messages waiting in each module's buffer.");
        Meter.CreateObservableCounter("meep.module.bytes", ObserveBytes, "By",
            "Bytes each module that talks to the network has read (network.io.direction receive) and written (transmit).");
    }

    /// <summary>
    /// Makes sure the instruments exist, so a listener finds them once a pipeline runs. (Running the static
    /// constructor is all it does.)
    /// </summary>
    public static void Publish()
    {
    }

    /// <summary>Records how long a module took to handle a message, in <see cref="Stopwatch"/> ticks.</summary>
    public static void RecordDuration(Node node, long ticks)
    {
        var tags = new TagList { { "meep.pipeline", node.Pipeline.Name }, { "meep.module", node.Label } };
        if (node.Key is { } key)
            tags.Add("meep.module.key", key);
        Duration.Record(ticks / (double)Stopwatch.Frequency, tags);
    }

    private static IEnumerable<Measurement<long>> ObserveCounts()
    {
        foreach (var pipeline in Pipeline.AllRunning)
        {
            foreach (var node in pipeline.Nodes)
            {
                var stats = node.GetStats();
                yield return Measure(stats.Received, pipeline, node, "received");
                yield return Measure(stats.Handled, pipeline, node, "handled");
                yield return Measure(stats.Emitted, pipeline, node, "emitted");
                yield return Measure(stats.Passed, pipeline, node, "passed");
                yield return Measure(stats.Dropped, pipeline, node, "dropped");
                yield return Measure(stats.Filtered, pipeline, node, "filtered");
                yield return Measure(stats.Expired, pipeline, node, "expired");
                yield return Measure(stats.Refused, pipeline, node, "refused");
                yield return Measure(stats.Denied, pipeline, node, "denied");
            }
        }
    }

    private static IEnumerable<Measurement<long>> ObserveFailures()
    {
        foreach (var pipeline in Pipeline.AllRunning)
        {
            foreach (var node in pipeline.Nodes)
            {
                var stats = node.GetStats();
                yield return Measure(stats.Failed, pipeline, node, "failed");
                yield return Measure(stats.Restarts, pipeline, node, "restarted");
            }
        }
    }

    private static IEnumerable<Measurement<long>> ObserveQueues()
    {
        foreach (var pipeline in Pipeline.AllRunning)
        {
            foreach (var node in pipeline.Nodes)
                yield return new Measurement<long>(node.Queued, Tags(pipeline, node));
        }
    }

    private static IEnumerable<Measurement<long>> ObserveBytes()
    {
        foreach (var pipeline in Pipeline.AllRunning)
        {
            foreach (var node in pipeline.Nodes)
            {
                var traffic = node.Counters.Traffic;
                if (traffic == default)
                    continue; // modules that don't talk to the network
                yield return Measure(traffic.Received, pipeline, node, "receive", "network.io.direction");
                yield return Measure(traffic.Sent, pipeline, node, "transmit", "network.io.direction");
            }
        }
    }

    private static Measurement<long> Measure(long value, Pipeline pipeline, Node node, string happened, string tag = "meep.event")
    {
        var tags = Tags(pipeline, node);
        tags.Add(tag, happened);
        return new(value, tags);
    }

    // Each metric's pipeline and module, and the module's key if it has one.
    private static TagList Tags(Pipeline pipeline, Node node)
    {
        var tags = new TagList { { "meep.pipeline", pipeline.Name }, { "meep.module", node.Label } };
        if (node.Key is { } key)
            tags.Add("meep.module.key", key);
        return tags;
    }
}
