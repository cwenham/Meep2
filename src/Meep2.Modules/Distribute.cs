using System.Globalization;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Spreads messages across copies of the branch below it, so a slow module runs side by side, while messages with
/// the same key always take the same copy and keep their order (Q53). In Meeplang:
/// <c>Distribute 4 by msg.CustomerId</c>, with the branch indented under it.
/// </summary>
/// <remarks>
/// The loader makes the copies. Each copy's modules handle one message at a time, as any module does, and have their
/// own state. Without <c>by</c>, messages are spread by their ids, so order isn't kept. Distribute chooses a copy as
/// each message is delivered to it, so it never holds messages up.
/// </remarks>
[Verb("Distribute")]
[EmitsInput]
public sealed class Distribute(DistributeParameters parameters) : AInflection<Message>, IRouteMessages
{
    /// <inheritdoc/>
    public int Lanes => parameters.Copies;

    /// <inheritdoc/>
    public int LaneOf(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (parameters.By is not { } by)
            return message.Id.GetHashCode();
        var key = Convert.ToString(by.Evaluate(message), CultureInfo.InvariantCulture) ?? "";
        return StringComparer.Ordinal.GetHashCode(key);
    }

    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        // The runtime routes messages as they arrive, so this only runs if it's built some other way.
        context.Emit(message);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Parameters for <see cref="Distribute"/>.</summary>
public sealed record DistributeParameters
{
    /// <summary>How many copies of the branch to make, at least 2.</summary>
    [Positional]
    public required int Copies
    {
        get;
        init => field = value >= 2 ? value : throw new ArgumentOutOfRangeException(nameof(Copies), "Distribute needs at least 2 copies.");
    }

    /// <summary>The key that decides a message's copy, so messages with the same key keep their order.</summary>
    public Computed<object?>? By { get; init; }
}
