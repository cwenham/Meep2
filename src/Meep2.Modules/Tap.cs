using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Receives everything another module emits, as if it were indented under that module. In Meeplang:
/// <c>Tap Schedule</c>, or <c>Tap Schedule @errors</c> for its errors only.
/// </summary>
[Verb("Tap")]
[Role(VerbRole.Tap)]
[EmitsInput]
public sealed class Tap(TapParameters parameters) : AInflection<Message>
{
    /// <summary>The module being tapped.</summary>
    public ModuleReference From => parameters.From;

    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        context.Emit(message);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Parameters for <see cref="Tap"/>.</summary>
public sealed record TapParameters
{
    /// <summary>The name of the module to tap.</summary>
    [Positional]
    public required ModuleReference From { get; init; }
}
