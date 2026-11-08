using Meep2.SDK.Messages;

namespace Meep2.SDK.Modules;

/// <summary>
/// What a module uses to talk to its pipeline. The runtime provides it.
/// </summary>
public abstract class ModuleContext
{
    internal ModuleContext()
    {
    }

    /// <summary>The module's place in the pipeline.</summary>
    public abstract ModuleInfo Module { get; }

    /// <summary>
    /// Sends a message to every module downstream. A module that handles messages can only emit while it's
    /// handling one; a new message becomes a child of the message being handled.
    /// </summary>
    /// <remarks>Emitting never waits: if a downstream module's buffer is full, its oldest message is dropped.</remarks>
    public abstract void Emit(Message message);

    /// <summary>
    /// Emits a new message and reports when it, and every message made from it, has left the pipeline.
    /// Sources use this to acknowledge or commit what they read.
    /// </summary>
    /// <remarks>
    /// Don't wait for the result inside <c>HandleAsync</c> if messages downstream can come back to this module:
    /// it would be waiting for itself.
    /// </remarks>
    public abstract Task<MessageOutcome> EmitTracked(Message message);
}
