using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>
/// Meep's OpenTelemetry data, through .NET's own <see cref="ActivitySource"/> and <see cref="Meter"/> (Q43, Q64).
/// With nothing listening, tracing costs a check per message and the metrics cost nothing: most of them are read
/// from the counts each module keeps anyway.
/// </summary>
/// <remarks>
/// Each time a module handles a message is a span. Its parent is the span the message came from: the span of the
/// module that emitted it while handling another message, or a short span for a source's emitting it. So a trace
/// follows fam. A message made from several parents links to the others' spans.
/// </remarks>
internal static class Telemetry
{
    private static readonly string? s_version = typeof(Telemetry).Assembly.GetName().Version?.ToString();

    public static readonly ActivitySource Source = new(MeepTelemetry.Name, s_version);

    public static readonly Meter Meter = new(MeepTelemetry.Name, s_version);

    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "meep.module.duration", "s", "How long a module took to handle a message.");

    // The span each message came from, while tracing is on. Weak, so it lasts no longer than the message.
    private static readonly ConditionalWeakTable<Message, StrongBox<ActivityContext>> s_cameFrom = new();

    static Telemetry()
    {
        Meter.CreateObservableCounter("meep.module.messages", ObserveCounts, "{message}",
            "Messages through each module, by what happened to them: received, handled, emitted, dropped, filtered, expired or refused.");
        Meter.CreateObservableCounter("meep.module.failures", ObserveFailures, "{failure}",
            "Times each module threw, and times it was restarted.");
        Meter.CreateObservableUpDownCounter("meep.module.queued", ObserveQueues, "{message}",
            "Messages waiting in each module's buffer.");
    }

    /// <summary>Whether anything is collecting spans.</summary>
    public static bool Tracing => Source.HasListeners();

    /// <summary>Records the span a message came from, as it's emitted.</summary>
    public static void Emitted(Node from, ModuleInfo emitter, Message message)
    {
        ActivityContext cameFrom;
        if (from.Handling is { } handling && ReferenceEquals(handling.Message, message.Parent))
        {
            cameFrom = handling.Span.Context;
        }
        else if (from is SourceNode)
        {
            // A short span for the source's emitting the message, which roots its trace.
            using var span = Source.StartActivity(SpanName(emitter), ActivityKind.Producer);
            if (span is null)
                return;
            if (!span.Recorded)
            {
                // Not sampled. Nothing after it is either, so its messages needn't remember where they came from.
                return;
            }
            if (span.IsAllDataRequested)
                Describe(span, from.Pipeline, emitter, message);
            cameFrom = span.Context;
        }
        else if (message.Parent is { } parent && s_cameFrom.TryGetValue(parent, out var parentCameFrom))
        {
            cameFrom = parentCameFrom.Value;
        }
        else
        {
            return;
        }
        s_cameFrom.AddOrUpdate(message, new StrongBox<ActivityContext>(cameFrom));
    }

    /// <summary>Records an injected message as the start of a trace, as a source's messages are.</summary>
    public static void Injected(Pipeline pipeline, Message message)
    {
        using var span = Source.StartActivity(SpanName(Pipeline.Injector), ActivityKind.Producer);
        if (span is null || !span.Recorded)
            return;
        if (span.IsAllDataRequested)
            Describe(span, pipeline, Pipeline.Injector, message);
        s_cameFrom.AddOrUpdate(message, new StrongBox<ActivityContext>(span.Context));
    }

    /// <summary>
    /// Starts the span for a module handling a message, or returns null if nothing wants it. Traces start where
    /// messages enter the pipeline, so a message with no span to come from gets none: one that arrived before tracing
    /// started, or one whose trace wasn't sampled, which then costs no more than this check.
    /// </summary>
    public static Activity? StartHandling(Node node, ModuleInfo inflection, Message message)
    {
        if (!s_cameFrom.TryGetValue(message, out var cameFrom))
            return null;
        var parent = cameFrom.Value;
        if ((parent.TraceFlags & ActivityTraceFlags.Recorded) == 0)
            return null; // the source's span wasn't sampled, so neither is anything after it
        var span = Source.StartActivity(SpanName(inflection), ActivityKind.Internal, parent, tags: null, links: Links(message));
        if (span is null)
            return null;
        if (span.IsAllDataRequested)
            Describe(span, node.Pipeline, inflection, message);
        node.Handling = (message, span);
        return span;
    }

    /// <summary>Ends a module's span, marking it as failed if the module threw.</summary>
    public static void EndHandling(Node node, Activity span, MessageOutcome outcome, Exception? failure)
    {
        if (failure is not null)
        {
            span.SetStatus(ActivityStatusCode.Error, failure.Message);
            span.AddException(failure);
        }
        else if (outcome != MessageOutcome.Completed)
        {
            span.SetTag("meep.outcome", outcome.ToString());
        }
        span.Dispose();
        node.Handling = null;
    }

    private static void Describe(Activity span, Pipeline pipeline, ModuleInfo module, Message message)
    {
        span.SetTag("meep.pipeline", pipeline.Name);
        span.SetTag("meep.module", module.ToString());
        span.SetTag("meep.message.type", message.GetType().Name);
        span.SetTag("meep.message.id", message.Id.ToString());
        if (message is ErrorMessage error)
            span.SetTag("meep.error", error.Error);
    }

    // Span names are kept few, as OpenTelemetry likes: the verb and inflection, not the module's number.
    private static string SpanName(ModuleInfo module) =>
        module.Inflection is { } inflection && inflection != module.Verb ? $"{module.Verb}.{inflection}" : module.Verb;

    // A message with several parents links to the spans the others came from.
    private static List<ActivityLink>? Links(Message message)
    {
        if (message.Parents.Count < 2)
            return null;
        List<ActivityLink>? links = null;
        var first = true;
        foreach (var parent in message.Parents)
        {
            if (!first && s_cameFrom.TryGetValue(parent, out var cameFrom))
                (links ??= []).Add(new ActivityLink(cameFrom.Value));
            first = false;
        }
        return links;
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
                yield return Measure(stats.Dropped, pipeline, node, "dropped");
                yield return Measure(stats.Filtered, pipeline, node, "filtered");
                yield return Measure(stats.Expired, pipeline, node, "expired");
                yield return Measure(stats.Refused, pipeline, node, "refused");
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
                yield return new Measurement<long>(node.Queued, new KeyValuePair<string, object?>("meep.pipeline", pipeline.Name), new KeyValuePair<string, object?>("meep.module", node.Label));
        }
    }

    private static Measurement<long> Measure(long value, Pipeline pipeline, Node node, string happened) =>
        new(value,
            new KeyValuePair<string, object?>("meep.pipeline", pipeline.Name),
            new KeyValuePair<string, object?>("meep.module", node.Label),
            new KeyValuePair<string, object?>("meep.event", happened));
}
