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
}
