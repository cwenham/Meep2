using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>
/// Finishes a new message as it's emitted: records its emitter, gives it a parent if it has none, and works out its
/// expiry and tracking from its parents.
/// </summary>
internal static class Stamper
{
    /// <param name="defaultParent">The parent for a new message that doesn't name its own: the message being handled.</param>
    /// <param name="created">Collects tracking scopes made for new errors. The caller releases them after delivery.</param>
    public static void Stamp(
        Message message,
        ModuleInfo emitter,
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
            Stamp(parent, emitter, defaultParent, pipeline, ref created);
            expires = Deadline.Earliest(expires, parent.ExpiresTimestamp);
            tracking = ScopeSet.Union(tracking, parent.Tracking);
        }

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
}
