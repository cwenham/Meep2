using Meep2.SDK.Messages;

namespace Meep2.SDK.Modules;

/// <summary>
/// A module that handles incoming messages. Derive from <see cref="AInflection{TIn}"/>; this non-generic base exists
/// so that pipelines can refer to any such module.
/// </summary>
public abstract class AInflection : AModule
{
    private protected AInflection()
    {
    }

    internal abstract Type InputType { get; }

    internal virtual bool IsPassthrough => false;

    internal abstract bool Accepts(Message message);

    internal abstract ValueTask Handle(Message message, ModuleContext context, CancellationToken cancellationToken);
}

/// <summary>
/// A module that handles messages of type <typeparamref name="TIn"/>, one at a time and in order.
/// </summary>
/// <typeparam name="TIn">
/// The most general type the module can handle: a message type, or an interface that message types implement.
/// The runtime never hands the module anything else.
/// </typeparam>
/// <remarks>
/// A module emits zero or more messages for each one it handles, by calling <see cref="ModuleContext.Emit"/>.
/// New messages become children of the message being handled. Emitting the message it was given passes it on
/// unchanged. If the module throws, the runtime emits an <see cref="ErrorMessage"/> in its place, then disposes the
/// module and creates a fresh one for the next message.
/// </remarks>
public abstract class AInflection<TIn> : AInflection
    where TIn : class
{
    /// <summary>Handles one message.</summary>
    protected abstract ValueTask HandleAsync(TIn message, ModuleContext context, CancellationToken cancellationToken);

    internal sealed override Type InputType => typeof(TIn);

    internal sealed override bool Accepts(Message message) => message is TIn;

    internal sealed override ValueTask Handle(Message message, ModuleContext context, CancellationToken cancellationToken) =>
        HandleAsync((TIn)(object)message, context, cancellationToken);
}
