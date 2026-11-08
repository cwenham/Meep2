using Meep2.SDK.Messages;

namespace Meep2.SDK.Modules;

/// <summary>
/// A module whose work doesn't change the message, such as writing it to a file. The runtime passes each message
/// on downstream straight away, and the module handles it on its own thread, so it never holds the pipeline up.
/// </summary>
/// <remarks>
/// Messages the module can't handle are not passed on, as with any other module. Like other modules, it handles
/// one message at a time; when it falls behind, its buffer drops the oldest messages. Because errors it takes are
/// passed on too, it only handles them when no module comes after it.
/// </remarks>
public abstract class APassthrough<TIn> : AInflection<TIn>
    where TIn : class
{
    internal sealed override bool IsPassthrough => true;
}
