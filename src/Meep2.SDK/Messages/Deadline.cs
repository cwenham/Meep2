using System.Diagnostics;
using System.Globalization;

namespace Meep2.SDK.Messages;

/// <summary>
/// A moment measured on the monotonic clock, so it isn't thrown off when the system clock changes.
/// </summary>
public readonly struct Deadline : IEquatable<Deadline>, IComparable<Deadline>
{
    internal Deadline(long timestamp) => Timestamp = Math.Max(timestamp, 1);

    /// <summary>A <see cref="Stopwatch"/> timestamp, never 0, so 0 can mean "no deadline".</summary>
    internal long Timestamp { get; }

    /// <summary>The deadline <paramref name="delay"/> from now.</summary>
    public static Deadline After(TimeSpan delay) => new(Add(Stopwatch.GetTimestamp(), delay));

    /// <summary>The deadline at a time on the system clock.</summary>
    public static Deadline At(DateTime time) => After(time.ToUniversalTime() - DateTime.UtcNow);

    /// <summary>Whether the deadline has passed.</summary>
    public bool HasPassed => Stopwatch.GetTimestamp() >= Timestamp;

    /// <summary>Time left before the deadline, or zero if it has passed.</summary>
    public TimeSpan Remaining
    {
        get
        {
            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), Timestamp);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>The deadline as a time on the system clock, for display.</summary>
    public DateTime ToUniversalTime() =>
        DateTime.UtcNow + Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), Timestamp);

    /// <summary>The earlier of two deadlines, where null means none.</summary>
    public static Deadline? Earliest(Deadline? first, Deadline? second) =>
        first is null ? second : second is null ? first : first.Value < second.Value ? first : second;

    /// <summary>The earlier of two timestamps, where 0 means none.</summary>
    internal static long Earliest(long first, long second) =>
        first == 0 ? second : second == 0 ? first : Math.Min(first, second);

    /// <inheritdoc/>
    public bool Equals(Deadline other) => Timestamp == other.Timestamp;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Deadline other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Timestamp.GetHashCode();

    /// <inheritdoc/>
    public int CompareTo(Deadline other) => Timestamp.CompareTo(other.Timestamp);

    /// <summary>The deadline as UTC time on the system clock, in ISO 8601 format.</summary>
    public override string ToString() => ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>True when both are the same moment.</summary>
    public static bool operator ==(Deadline left, Deadline right) => left.Equals(right);

    /// <summary>True when they're different moments.</summary>
    public static bool operator !=(Deadline left, Deadline right) => !left.Equals(right);

    /// <summary>True when <paramref name="left"/> comes first.</summary>
    public static bool operator <(Deadline left, Deadline right) => left.Timestamp < right.Timestamp;

    /// <summary>True when <paramref name="left"/> comes last.</summary>
    public static bool operator >(Deadline left, Deadline right) => left.Timestamp > right.Timestamp;

    /// <summary>True unless <paramref name="left"/> comes last.</summary>
    public static bool operator <=(Deadline left, Deadline right) => left.Timestamp <= right.Timestamp;

    /// <summary>True unless <paramref name="left"/> comes first.</summary>
    public static bool operator >=(Deadline left, Deadline right) => left.Timestamp >= right.Timestamp;

    // Adds a delay to a timestamp, stopping at the limits instead of overflowing.
    private static long Add(long timestamp, TimeSpan delay)
    {
        var ticks = (Int128)delay.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
        var sum = timestamp + ticks;
        return sum > long.MaxValue ? long.MaxValue : sum < 1 ? 1 : (long)sum;
    }
}
