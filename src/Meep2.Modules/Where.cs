using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Passes on only the messages a condition is true for. In Meeplang: <c>Where msg.minutesAway &lt; 30</c>.
/// </summary>
/// <remarks>Messages whose type the condition doesn't fit skip Where and carry on, as with <c>@bypass</c>.</remarks>
[Verb("Where")]
[Role(VerbRole.Filter)]
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
