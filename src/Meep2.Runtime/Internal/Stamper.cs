using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>
/// Finishes a new message as it's emitted: records its emitter, gives it a parent if it has none, and works out its
/// expiry and tracking from its parents.
/// </summary>
internal static class Stamper
{
    /// <param name="message">The message being emitted.</param>
    /// <param name="emitter">The module, or the inflection of it, that emitted the message.</param>
    /// <param name="from">The module's place in the pipeline, for its <c>@expires</c>.</param>
    /// <param name="defaultParent">The parent for a new message that doesn't name its own: the message being handled.</param>
    /// <param name="pipeline">The pipeline, for its default expiry and for tracking errors.</param>
    /// <param name="created">Collects tracking scopes made for new errors. The caller releases them after delivery.</param>
    public static void Stamp(
        Message message,
        ModuleInfo emitter,
        Node from,
        Message? defaultParent,
        Pipeline pipeline,
        ref List<TrackingScope>? created)
    {
        // A message that's been emitted before is passed on as it is.
        if (message.IsStamped)
            return;

        if (message.Parents.Count == 0 && defaultParent is not null)
            message.SetParents(new Parents(defaultParent));

        var expires = message.ExpiresTimestamp;
        object? tracking = null;
        foreach (var parent in message.Parents)
        {
            // A parent the module made but didn't emit, such as an intermediate copy, is stamped first.
            Stamp(parent, emitter, from, defaultParent, pipeline, ref created);
            expires = Deadline.Earliest(expires, parent.ExpiresTimestamp);
            tracking = ScopeSet.Union(tracking, parent.Tracking);
        }

        // @expires replaces what the message would inherit. A source's own messages get the pipeline's default.
        if (from.Options.Expires is { } lifetime)
            expires = lifetime == Timeout.InfiniteTimeSpan ? 0 : Deadline.After(lifetime).Timestamp;
        else if (expires == 0 && from is SourceNode && pipeline.Options.Expires is { } defaultLifetime)
            expires = Deadline.After(defaultLifetime).Timestamp;

        if (message is ErrorMessage error)
        {
            // Every error is tracked, so the runtime can tell whether anything handled it.
            var scope = new ErrorScope(error, pipeline);
            (created ??= []).Add(scope);
            tracking = ScopeSet.Prepend(scope, tracking);
        }

        message.ExpiresTimestamp = expires;
        message.Tracking = tracking;
        message.EmittedBy = emitter;
    }

    /// <summary>Finishes a message sent in from outside, as a test: it keeps its own expiry, and an error is tracked.</summary>
    public static void StampInjected(Message message, Pipeline pipeline, ref List<TrackingScope>? created)
    {
        if (message is ErrorMessage error)
        {
            var scope = new ErrorScope(error, pipeline);
            (created ??= []).Add(scope);
            message.Tracking = scope;
        }
        message.EmittedBy = Pipeline.Injector;
    }
}
