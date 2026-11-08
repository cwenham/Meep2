using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Passes on only the messages a condition is true for. In Meeplang: <c>Where msg.minutesAway &lt; 30</c>.
/// </summary>
/// <remarks>Messages whose type the condition doesn't fit skip Where and carry on, as with <c>@bypass</c>.</remarks>
[Verb("Where")]
[EmitsInput]
public sealed class Where(WhereParameters parameters) : AInflection<Message>
{
    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (parameters.Condition.Evaluate(message))
                context.Emit(message);
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Parameters for <see cref="Where"/>.</summary>
public sealed record WhereParameters
{
    /// <summary>The condition, worked out for each message.</summary>
    [Positional]
    public required Computed<bool> Condition { get; init; }
}

/// <summary>
/// Receives everything another module emits, as if it were indented under that module. In Meeplang:
/// <c>Tap Schedule</c>, or <c>Tap Schedule @errors</c> for its errors only.
/// </summary>
[Verb("Tap")]
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
