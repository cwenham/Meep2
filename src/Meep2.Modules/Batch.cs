using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Gathers messages into a <see cref="Batched"/> message: so many at a time, or whatever has come within a while of the
/// first, whichever is first. In Meeplang: <c>Batch 100</c>, <c>Batch within=1s</c>, or <c>Batch 100 within=1s</c>.
/// </summary>
/// <remarks>
/// The messages in a batch stay in flight, with their leases open, until the batch and everything made from it has
/// finished, so a batch of queue messages is acknowledged only once it's done with (round 3, section 5). When the
/// pipeline has nothing else left to do, a part-filled batch goes out as it is.
/// </remarks>
[Verb("Batch")]
[Emits(typeof(Batched))]
public sealed class Batch(BatchParameters parameters) : AInflection<Message>, IChosenByParameters<BatchParameters>
{
    private readonly List<Message> _items = [];

    /// <summary>Needs a size, a time, or both.</summary>
    public static string? WhyNot(BatchParameters parameters, Message message)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.Size is null && parameters.Within is null ? "needs a size, as in Batch 100, or a time, as in Batch within=1s" : null;
    }

    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        context.Keep(message);
        _items.Add(message);
        if (_items.Count == 1 && parameters.Within is { } within)
            context.FlushAfter(within);
        if (_items.Count >= parameters.Size)
            Send(context);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    protected override ValueTask FlushAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        if (_items.Count > 0)
            Send(context);
        return ValueTask.CompletedTask;
    }

    private void Send(ModuleContext context)
    {
        context.Emit(new Batched { Parents = new Parents(_items) });
        _items.Clear();
    }
}

/// <summary>Parameters for <see cref="Batch"/>.</summary>
public sealed record BatchParameters
{
    /// <summary>How many messages make a batch.</summary>
    [Positional]
    public int? Size
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(Size), "A batch needs room for at least 1 message.");
    }

    /// <summary>How long after its first message a batch goes out, however few it has.</summary>
    public TimeSpan? Within
    {
        get;
        init => field = value is null || value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(Within), "within must be longer than zero.");
    }
}
