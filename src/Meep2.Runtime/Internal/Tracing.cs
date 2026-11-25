using System.Diagnostics;
using System.Runtime.CompilerServices;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Security;

namespace Meep2.Runtime.Internal;

/// <summary>
/// Meep's OpenTelemetry spans, through .NET's own <see cref="ActivitySource"/> (Q43, Q64). With nothing listening,
/// tracing costs a check per message.
/// </summary>
/// <remarks>
/// Each time a module handles a message is a span. Its parent is the span the message came from: the span of the
/// module that emitted it while handling another message, or a short span for a source's emitting it. So a trace
/// follows fam. A message made from several parents links to the others' spans.
/// </remarks>
internal static class Tracing
{
    public static readonly ActivitySource Source = new(MeepTelemetry.Name, typeof(Tracing).Assembly.GetName().Version?.ToString());

    // The span each message came from, while tracing is on. Weak, so it lasts no longer than the message.
    private static readonly ConditionalWeakTable<Message, StrongBox<ActivityContext>> s_cameFrom = new();

    // The refusal span each PermissionDenied came from, so its trace hangs under the refusal (Q298).
    private static readonly ConditionalWeakTable<Message, StrongBox<ActivityContext>> s_refusals = new();

    // The span on another host that a message a source is about to emit continues (ContinueTrace).
    private static readonly ConditionalWeakTable<Message, StrongBox<ActivityContext>> s_remote = new();

    /// <summary>Whether anything is collecting spans.</summary>
    public static bool IsOn => Source.HasListeners();

    /// <summary>Records the span a message came from, as it's emitted.</summary>
    public static void Emitted(Node from, ModuleInfo emitter, Message message)
    {
        ActivityContext cameFrom;
        if (message is PermissionDenied && s_refusals.TryGetValue(message, out var refusal))
        {
            cameFrom = refusal.Value;
        }
        else if (from.Handling is { } handling && ReferenceEquals(handling.Message, message.Parent))
        {
            cameFrom = handling.Span.Context;
        }
        else if (from is SourceNode)
        {
            // A short span for the source's emitting the message, which roots its trace, or continues one from
            // another host, as a request from another Meep does.
            using var span = s_remote.TryGetValue(message, out var remote)
                ? Source.StartActivity(SpanName(emitter), ActivityKind.Server, remote.Value)
                : Source.StartActivity(SpanName(emitter), ActivityKind.Producer);
            if (span is null)
                return;
            if (!span.Recorded)
            {
                // Not sampled. Nothing after it is either, so its messages needn't remember where they came from.
                return;
            }
            if (span.IsAllDataRequested)
                Describe(span, from.Pipeline, emitter, from.Key, message);
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

    /// <summary>
    /// Records that a message a source is about to emit continues a trace from another host, by its W3C
    /// <c>traceparent</c> (<see cref="ModuleContext.ContinueTrace"/>). Text that isn't one is ignored.
    /// </summary>
    public static void Continue(Message message, string traceParent)
    {
        if (ActivityContext.TryParse(traceParent, null, isRemote: true, out var context))
            s_remote.AddOrUpdate(message, new StrongBox<ActivityContext>(context));
    }

    /// <summary>
    /// The W3C <c>traceparent</c> for a request made while a module handles a message: its span, if the message is
    /// traced; one marked as not recorded, with new IDs, if it isn't, so the other host doesn't record its part.
    /// </summary>
    public static string TraceParent(Node node, Message? message)
    {
        if (node.Handling is { } handling && ReferenceEquals(handling.Message, message) && handling.Span.IdFormat == ActivityIdFormat.W3C)
            return handling.Span.Id!;
        return $"00-{ActivityTraceId.CreateRandom().ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-00";
    }

    /// <summary>
    /// Records a refusal as a short span of its own (Q298), named after the module that refused, as in
    /// <c>Save.denied</c>, whose parent is the span the message came from, as a handling span's is, so every refusal is
    /// in the trace, one straight after a source too. It carries the module, the message, the permissions it lacked
    /// and why, and <c>meep.outcome = denied</c>, and an event, <c>meep.permission.denied</c>, saying the same. Its
    /// status isn't an error, since a refusal isn't a fault. The <paramref name="denial"/> it becomes, if any, hangs
    /// under it, so an <c>@errors</c> branch answering it is its child. Nothing is recorded for a message whose trace
    /// isn't, as for handling spans.
    /// </summary>
    /// <param name="gate">The node that refused the message.</param>
    /// <param name="module">The module, or inflection, that refused it, which names the span.</param>
    /// <param name="message">The message refused.</param>
    /// <param name="refusal">What it lacked, and why.</param>
    /// <param name="denial">The <see cref="PermissionDenied"/> it becomes, about to be emitted, or null for an error passed by.</param>
    public static void Denied(Node gate, ModuleInfo module, Message message, Verifying.Refusal refusal, PermissionDenied? denial)
    {
        if (!s_cameFrom.TryGetValue(message, out var cameFrom) || (cameFrom.Value.TraceFlags & ActivityTraceFlags.Recorded) == 0)
            return;
        using var span = Source.StartActivity(SpanName(module) + ".denied", ActivityKind.Internal, cameFrom.Value, tags: null, links: Links(message));
        if (span is null)
            return;
        if (span.IsAllDataRequested)
        {
            Describe(span, gate.Pipeline, module, gate.Key, message);
            var permissions = string.Join(" ", refusal.Needed);
            var reasons = string.Join(" ", refusal.Reasons);
            span.SetTag("meep.permission", permissions);
            span.SetTag("meep.permission.reasons", reasons);
            span.SetTag("meep.outcome", "denied");
            span.AddEvent(new ActivityEvent("meep.permission.denied", tags: new ActivityTagsCollection
            {
                { "meep.module", gate.Label },
                { "meep.message.id", message.Id.ToString() },
                { "meep.permission", permissions },
                { "meep.permission.reasons", reasons },
            }));
        }
        if (denial is not null && span.Recorded)
            s_refusals.AddOrUpdate(denial, new StrongBox<ActivityContext>(span.Context));
    }

    /// <summary>Records an injected message as the start of a trace, as a source's messages are.</summary>
    public static void Injected(Pipeline pipeline, Message message)
    {
        using var span = Source.StartActivity(SpanName(Pipeline.Injector), ActivityKind.Producer);
        if (span is null || !span.Recorded)
            return;
        if (span.IsAllDataRequested)
            Describe(span, pipeline, Pipeline.Injector, null, message);
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
            Describe(span, node.Pipeline, inflection, node.Key, message);
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

    private static void Describe(Activity span, Pipeline pipeline, ModuleInfo module, string? key, Message message)
    {
        span.SetTag("meep.pipeline", pipeline.Name);
        span.SetTag("meep.module", module.ToString());
        if (key is not null)
            span.SetTag("meep.module.key", key);
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
}
