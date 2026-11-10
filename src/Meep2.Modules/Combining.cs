using System.Diagnostics;
using System.Globalization;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>What <see cref="Batch"/> emits: messages that went together. Its items are its parents (round 3, section 5).</summary>
public sealed record Batched : Message
{
    /// <summary>The messages in the batch, in the order they came: a view of <see cref="Message.Parents"/>, not a copy.</summary>
    public IReadOnlyList<Message> Items => Parents;

    /// <summary>How many messages are in the batch.</summary>
    public int Count => Parents.Count;
}

/// <summary>What <see cref="Zip"/> emits: a message from each of its two inputs. They're its parents, left first.</summary>
/// <param name="Left">The message from input 1.</param>
/// <param name="Right">The message from input 2.</param>
public sealed record Zipped(Message Left, Message Right) : Message;

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

/// <summary>
/// Pairs a message from its first input with one from its second, as a <see cref="Zipped"/> message: in the order
/// they come, or by a key both sides share. In Meeplang, with two <c>+</c> lines feeding it: <c>Zip</c>, or
/// <c>Zip by msg.OrderId</c> to pair an order with its payment.
/// </summary>
/// <remarks>
/// A message waiting for its partner stays in flight, with its leases open. It becomes an error saying nothing came
/// to pair with it (Q131):
/// <list type="bullet">
/// <item>when it has waited <c>within</c>, or its own deadline passes, whichever is sooner;</item>
/// <item>when more than <c>limit</c> messages are waiting and it has waited longest, the limit being the pipeline's
/// buffer size unless it's given;</item>
/// <item>when the pipeline has nothing else left to do.</item>
/// </list>
/// </remarks>
[Verb("Zip")]
[Emits(typeof(Zipped))]
public sealed class Zip(ZipParameters parameters) : AInflection<Message>
{
    private const string InOrder = "";

    // Messages waiting for a partner, from inputs 1 and 2, by key. Each is also in the order they came, and if it
    // has a time to give up, in order of that time. Each of these skips a message that has gone.
    private readonly Dictionary<string, Queue<Waiting>>[] _waiting = [new(StringComparer.Ordinal), new(StringComparer.Ordinal)];
    private readonly Queue<Waiting> _arrived = new();
    private readonly PriorityQueue<Waiting, long> _due = new();
    private int _count;
    private long _flushAt = long.MaxValue;

    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        var input = context.Input;
        if (input is not (1 or 2))
        {
            context.EmitError($"Zip pairs messages from two inputs, its first and second, but this came in on input {input}.");
            return ValueTask.CompletedTask;
        }
        string key;
        try
        {
            key = parameters.By is { } by ? Convert.ToString(by.Evaluate(message), CultureInfo.InvariantCulture) ?? InOrder : InOrder;
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return ValueTask.CompletedTask;
        }

        if (Partner(2 - input, key) is { } partner)
        {
            var (left, right) = input == 1 ? (message, partner.Message) : (partner.Message, message);
            context.Emit(new Zipped(left, right) { Parents = [left, right] });
            Compact();
            return ValueTask.CompletedTask;
        }

        context.Keep(message);
        var waiting = Wait(message, input, key);
        if (waiting.Due != long.MaxValue)
        {
            _due.Enqueue(waiting, waiting.Due);
            FlushBy(waiting.Due, context);
        }
        var limit = parameters.Limit ?? context.BufferSize;
        while (_count > limit && Oldest() is { } oldest)
            GiveUp(oldest, context, $" before {limit} messages were waiting, Zip's limit");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    protected override ValueTask FlushAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        if (context.Finishing)
        {
            while (Oldest() is { } oldest)
                GiveUp(oldest, context, "");
            _due.Clear();
            _flushAt = long.MaxValue;
            return ValueTask.CompletedTask;
        }

        // A flush Zip asked for: messages whose time has come give up.
        _flushAt = long.MaxValue;
        var now = Stopwatch.GetTimestamp();
        while (_due.TryPeek(out var next, out var due))
        {
            if (next.Gone)
            {
                _due.Dequeue();
                continue;
            }
            if (due > now)
            {
                FlushBy(due, context);
                break;
            }
            _due.Dequeue();
            GiveUp(next, context, next.Expires ? " before it expired" : $" within {Written(parameters.Within!.Value)}");
        }
        Compact();
        return ValueTask.CompletedTask;
    }

    private Waiting Wait(Message message, int input, string key)
    {
        var due = long.MaxValue;
        var expires = false;
        var now = Stopwatch.GetTimestamp();
        if (parameters.Within is { } within)
            due = now + Ticks(within);
        if (message.Expires is { } deadline && now + Ticks(deadline.Remaining) < due)
        {
            due = now + Ticks(deadline.Remaining);
            expires = true;
        }
        var waiting = new Waiting(message, input, key, due, expires);
        var mine = _waiting[input - 1];
        if (!mine.TryGetValue(key, out var queue))
            mine[key] = queue = new Queue<Waiting>();
        queue.Enqueue(waiting);
        _arrived.Enqueue(waiting);
        _count++;
        return waiting;
    }

    // The message that has waited longest for a partner with this key on one side, which stops waiting.
    private Waiting? Partner(int side, string key)
    {
        var others = _waiting[side];
        if (!others.TryGetValue(key, out var queue))
            return null;
        Waiting? partner = null;
        while (partner is null && queue.TryDequeue(out var next))
        {
            if (!next.Gone)
                partner = next;
        }
        if (queue.Count == 0)
            others.Remove(key);
        if (partner is not null)
            Gone(partner);
        return partner;
    }

    private Waiting? Oldest()
    {
        while (_arrived.TryPeek(out var next))
        {
            if (!next.Gone)
                return next;
            _arrived.Dequeue();
        }
        return null;
    }

    // A message that stops waiting without a partner, as an error made from it.
    private void GiveUp(Waiting waiting, ModuleContext context, string why)
    {
        Gone(waiting);
        var mine = _waiting[waiting.Input - 1];
        if (mine.TryGetValue(waiting.Key, out var queue))
        {
            while (queue.TryPeek(out var first) && first.Gone)
                queue.Dequeue();
            if (queue.Count == 0)
                mine.Remove(waiting.Key);
        }
        var which = waiting.Key == InOrder ? "" : $" with the key {waiting.Key}";
        context.Emit(new ErrorMessage { Error = $"Nothing came on Zip's input {3 - waiting.Input} to pair with this message{which}{why}.", Parents = [waiting.Message] });
    }

    private void Gone(Waiting waiting)
    {
        waiting.Gone = true;
        _count--;
    }

    // Asks for a flush by a time, unless one is already due sooner.
    private void FlushBy(long due, ModuleContext context)
    {
        if (due >= _flushAt)
            return;
        _flushAt = due;
        context.FlushAfter(Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), due));
    }

    // Messages that paired away from the front of the queues stay in them until they reach it; rebuilds the queues
    // when they're mostly gone, so they can't grow while few messages wait.
    private void Compact()
    {
        if (_arrived.Count > 2 * _count + 64)
        {
            var live = _arrived.Where(w => !w.Gone).ToList();
            _arrived.Clear();
            foreach (var waiting in live)
                _arrived.Enqueue(waiting);
        }
        if (_due.Count > 2 * _count + 64)
        {
            var live = _due.UnorderedItems.Where(i => !i.Element.Gone).ToList();
            _due.Clear();
            _due.EnqueueRange(live);
        }
    }

    private static long Ticks(TimeSpan time) => (long)(time.TotalSeconds * Stopwatch.Frequency);

    private static string Written(TimeSpan time) => time switch
    {
        { TotalSeconds: < 1 } => $"{time.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}ms",
        { TotalMinutes: < 1 } => $"{time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s",
        { TotalHours: < 1 } => $"{time.TotalMinutes.ToString("0.###", CultureInfo.InvariantCulture)}m",
        _ => $"{time.TotalHours.ToString("0.###", CultureInfo.InvariantCulture)}h",
    };

    private sealed class Waiting(Message message, int input, string key, long due, bool expires)
    {
        public Message Message { get; } = message;
        public int Input { get; } = input;
        public string Key { get; } = key;
        public long Due { get; } = due;

        // Whether the message's own deadline comes before within.
        public bool Expires { get; } = expires;

        public bool Gone { get; set; }
    }
}

/// <summary>Parameters for <see cref="Zip"/>.</summary>
public sealed record ZipParameters
{
    /// <summary>The key that pairs a message from one input with one from the other. Without it, they pair in the order they come.</summary>
    public Computed<object?>? By { get; init; }

    /// <summary>
    /// How many messages can wait for a partner at once. When more are waiting, the one that has waited longest
    /// becomes an error. Without it, the pipeline's buffer size.
    /// </summary>
    public int? Limit
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(Limit), "limit needs room for at least 1 message.");
    }

    /// <summary>How long a message waits for its partner before it becomes an error. Without it, it waits until its own deadline, if it has one.</summary>
    public TimeSpan? Within
    {
        get;
        init => field = value is null || value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(Within), "within must be longer than zero.");
    }
}

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
