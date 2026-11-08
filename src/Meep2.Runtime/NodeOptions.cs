namespace Meep2.Runtime;

/// <summary>How the runtime treats one module in a pipeline. These are Meeplang's <c>@</c> modifiers.</summary>
public sealed record NodeOptions
{
    /// <summary>No overrides.</summary>
    public static NodeOptions Default { get; } = new();

    /// <summary>
    /// How many messages can wait for this module before the oldest is dropped. Null uses the pipeline's size.
    /// Meeplang's <c>@buffer</c>.
    /// </summary>
    public int? BufferSize { get; init; }

    /// <summary>
    /// Lets messages the module can't handle skip it and carry on downstream, instead of stopping there.
    /// Meeplang's <c>@bypass</c>.
    /// </summary>
    public bool Bypass { get; init; }

    /// <summary>Whether the module receives error messages.</summary>
    public ErrorDelivery Errors { get; init; } = ErrorDelivery.Include;

    /// <summary>
    /// Narrows which message types the module receives. Messages of a type this returns false for skip the module
    /// and carry on downstream, as with <see cref="Bypass"/>. Meeplang uses it so that a module only receives
    /// messages its expressions can be worked out for. The answer for each type is remembered.
    /// </summary>
    public Func<Type, bool>? HandlesType { get; init; }

    /// <summary>
    /// Sets the expiry of the messages this module makes: they expire this long after they're made, instead of
    /// inheriting their parents' expiry. <see cref="Timeout.InfiniteTimeSpan"/> clears it, so they never expire.
    /// Null leaves expiry as inherited. Meeplang's <c>@expires=5s</c> and <c>@expires=none</c>.
    /// </summary>
    /// <remarks>A message the module passes on unchanged keeps its expiry.</remarks>
    public TimeSpan? Expires { get; init; }

    /// <summary>
    /// Lets expired messages reach the module, for cleaning up or looking into faults. The module's cancellation token
    /// then doesn't fire when a message expires. Meeplang's <c>@accept-expired</c>.
    /// </summary>
    public bool AcceptExpired { get; init; }

    /// <summary>The error policy for this module and the branch below it. Null uses the pipeline's. Meeplang's <c>@on-error</c>.</summary>
    public ErrorPolicy? ErrorPolicy { get; init; }
}
