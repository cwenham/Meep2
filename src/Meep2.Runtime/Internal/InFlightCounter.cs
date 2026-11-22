namespace Meep2.Runtime.Internal;

/// <summary>
/// Counts everything that keeps a pipeline alive: messages in buffers and handlers, running sources, and the
/// startup hold. The pipeline halts when it reaches zero.
/// </summary>
internal sealed class InFlightCounter
{
    private const long HaltedCount = long.MinValue / 2;

    private long _count;

    public void Add() => Interlocked.Increment(ref _count);

    /// <summary>Adds a count unless the pipeline has halted. Returns whether it did.</summary>
    public bool TryAdd()
    {
        while (true)
        {
            var count = Volatile.Read(ref _count);
            if (count < 0)
                return false;
            if (Interlocked.CompareExchange(ref _count, count + 1, count) == count)
                return true;
        }
    }

    /// <summary>Releases one count. Returns true when the count reaches zero, so the pipeline may halt.</summary>
    public bool Release() => Interlocked.Decrement(ref _count) == 0;

    /// <summary>Halts, if the count is still zero. Returns true exactly once.</summary>
    public bool TryHalt() => Interlocked.CompareExchange(ref _count, HaltedCount, 0) == 0;
}
