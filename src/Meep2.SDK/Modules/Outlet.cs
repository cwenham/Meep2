using Meep2.SDK.Messages;

namespace Meep2.SDK.Modules;

/// <summary>
/// A way for a module that handles messages to emit at any time, from any thread, rather than only while it handles
/// one, such as messages arriving on a connection it opened. Get one from <see cref="ModuleContext.Outlet"/>.
/// </summary>
/// <remarks>
/// <para>
/// Messages emitted through it have no parent unless they name their own <see cref="Message.Parents"/>, and record
/// the module, or its inflection, that the outlet was taken from.
/// </para>
/// <para>
/// Once the pipeline has finished, emitting does nothing and returns false: a pipeline finishes when its sources have
/// and no messages are left in it, so an outlet doesn't keep it running.
/// </para>
/// </remarks>
public abstract class Outlet
{
    internal Outlet()
    {
    }

    /// <summary>Sends a message to every module downstream. False if the pipeline has finished, so nothing was sent.</summary>
    public abstract bool Emit(Message message);

    /// <summary>
    /// Emits a new message carrying a lease, as <see cref="ModuleContext.Emit(Message, Lease)"/> does. The module stops
    /// holding the lease when it's first emitted, so hold it with <see cref="Lease.Hold"/> for as long as it should
    /// stay open between messages. False if the pipeline has finished.
    /// </summary>
    public abstract bool Emit(Message message, Lease lease);

    /// <summary>Emits an error. False if the pipeline has finished.</summary>
    public bool EmitError(string error, Exception? exception = null) =>
        Emit(new ErrorMessage { Error = error, Exception = exception });
}
