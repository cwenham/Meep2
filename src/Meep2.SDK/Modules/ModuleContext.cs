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
    /// Cancelled when the pipeline is asked to stop. Messages already in the pipeline still get time to finish, so
    /// most modules can ignore this. A module that waits a long time, such as one running another pipeline, can use
    /// it to wind down.
    /// </summary>
    public abstract CancellationToken Stopping { get; }

    /// <summary>
    /// Sends a message to every module downstream. A module that handles messages can only emit while it's
    /// handling one; a new message becomes a child of the message being handled.
    /// </summary>
    /// <remarks>Emitting never waits: if a downstream module's buffer is full, its oldest message is dropped.</remarks>
    public abstract void Emit(Message message);

    /// <summary>
    /// Emits a new message carrying a lease, such as a request waiting for its reply. The lease stays open while the
    /// message, or any message made from it, is in the pipeline, and while anything holds it. A lease can go out
    /// with several messages, such as each line read from one connection.
    /// </summary>
    public abstract void Emit(Message message, Lease lease);

    /// <summary>
    /// Emits a new message and reports when it, and every message made from it, has left the pipeline.
    /// Sources use this to acknowledge or commit what they read.
    /// </summary>
    /// <remarks>
    /// Don't wait for the result inside <c>HandleAsync</c> if messages downstream can come back to this module:
    /// it would be waiting for itself.
    /// </remarks>
    public abstract Task<MessageOutcome> EmitTracked(Message message);

    /// <summary>
    /// Emits an error in place of the message being handled. Modules use this for failures they expect, such as a
    /// server that doesn't answer, so the runtime doesn't have to replace them.
    /// </summary>
    public void EmitError(string error, Exception? exception = null) =>
        Emit(new ErrorMessage { Error = error, Exception = exception });

    /// <summary>
    /// Says the module has answered an error for whoever was waiting, such as by sending a reply to the request it
    /// came from. Taking the error then doesn't count as handling it: unless a module after this one handles it, the
    /// error policy logs it, but doesn't count it towards stopping the pipeline (Q100). So a server that answers bad
    /// requests keeps a record of them, and can't be stopped by them.
    /// </summary>
    public void Answered(ErrorMessage error)
    {
        ArgumentNullException.ThrowIfNull(error);
        error.AnsweredBy = Module;
    }
}
