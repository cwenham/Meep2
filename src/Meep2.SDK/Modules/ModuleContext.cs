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
    /// The name of the module's pipeline: its file's name without the extension, such as <c>nextbus</c> for
    /// <c>nextbus.meep</c>. Modules that keep things for their pipeline, such as Learn's classification trees, use it to
    /// tell pipelines apart.
    /// </summary>
    public virtual string PipelineName => "Pipeline";

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
    /// Which of the module's inputs the message being handled came in on, counting from 1 in the order the lines
    /// feeding the module are written (round 3, section 2). A module fed by one line always sees 1; a message sent in
    /// as a test, 0. Zip uses it to tell left from right.
    /// </summary>
    public virtual int Input => 1;

    /// <summary>
    /// Keeps a message after the module has finished handling it, as Batch and Zip do while they wait for the
    /// messages that go with it. It stays in flight, with its leases open, until the module emits a message made from
    /// it (one with it among its <see cref="Message.Parents"/>) or lets it go with <see cref="LetGo"/>.
    /// </summary>
    /// <remarks>
    /// When nothing else is left for the pipeline to do, as when its sources have finished or it's stopping, the
    /// runtime asks each module that keeps messages to flush them (<see cref="AInflection.FlushAsync"/>). Messages
    /// still kept after that are let go as the pipeline halts.
    /// </remarks>
    public virtual void Keep(Message message) =>
        throw new NotSupportedException("Only a module that handles messages can keep them.");

    /// <summary>Stops keeping a message, which then finishes, unless a message made from it is still in flight.</summary>
    public virtual void LetGo(Message message) =>
        throw new NotSupportedException("Only a module that handles messages can keep them.");

    /// <summary>
    /// Asks the runtime to flush the module after a while (<see cref="AInflection.FlushAsync"/>), between messages,
    /// as a batch that's due to go out after a second does. A later request replaces an earlier one.
    /// </summary>
    public virtual void FlushAfter(TimeSpan delay) =>
        throw new NotSupportedException("Only a module that handles messages can be flushed.");

    /// <summary>
    /// While flushing, whether it's because nothing else is left for the pipeline to do, as when its sources have
    /// finished or it's stopping: the last chance to emit the messages the module keeps. False for a flush the module
    /// asked for with <see cref="FlushAfter"/>, and while handling a message.
    /// </summary>
    public virtual bool Finishing => false;

    /// <summary>
    /// How many messages the module's buffer holds, as set by <c>buffer</c> under <c>defaults</c>. A module that keeps messages can take
    /// it as its limit on how many to keep, as Zip does (Q131).
    /// </summary>
    public virtual int BufferSize => 1024;

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

    /// <summary>
    /// Says the module left an error alone, as Send does when it has nowhere sensible to send one. Taking the error
    /// then doesn't count as handling it, even as the last module in its branch, so it still reaches the error policy
    /// unless a module after this one handles it.
    /// </summary>
    public void LeftAlone(ErrorMessage error)
    {
        ArgumentNullException.ThrowIfNull(error);
        error.LeftAloneBy = Module;
    }

    /// <summary>
    /// Emits at any time, from any thread, for a module that handles messages, such as one passing on what arrives on a
    /// connection it opened. Take it while handling a message, so what it emits records the inflection doing the work.
    /// Sources emit through the context itself at any time, so they don't need one.
    /// </summary>
    public virtual Outlet Outlet => throw new NotSupportedException("Only a module that handles messages has an outlet.");

    /// <summary>
    /// Things the modules of one pipeline share, by name, such as the connections a Listen has open, which Send reaches
    /// by the Listen's name. A plugin should start its names with its own, so they don't clash.
    /// </summary>
    public virtual System.Collections.Concurrent.ConcurrentDictionary<string, object> Shared =>
        throw new NotSupportedException("This context isn't part of a pipeline.");
}
