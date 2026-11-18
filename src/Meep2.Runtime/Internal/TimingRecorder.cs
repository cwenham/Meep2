using System.Diagnostics;

namespace Meep2.Runtime.Internal;

/// <summary>
/// Records a module's handling times in an exponential histogram (<see cref="HandlingTimes"/>), with no allocation and
/// no locked instructions.
/// </summary>
/// <remarks>
/// Only the module's own loop records, one message at a time, so the counts are plain increments; readers on other
/// threads may see a count a moment late, never a torn one, since each is a single aligned 64-bit value.
/// </remarks>
internal sealed class TimingRecorder
{
    /// <summary>Eight buckets to each doubling.</summary>
    public const int Scale = 3;

    // 1 ns is about bucket -240, and 2^16 seconds (18 hours) is bucket 127.
    private const int Lowest = -256;
    private const int Highest = 127;

    private static readonly double s_secondsPerTick = 1.0 / Stopwatch.Frequency;

    // Where each doubling divides: 2^(1/8) to 2^(7/8), for mantissas from 1 to 2.
    private static readonly double[] s_divisions = [.. Enumerable.Range(1, (1 << Scale) - 1).Select(k => Math.Pow(2, k / (double)(1 << Scale)))];

    private readonly long[] _buckets = new long[Highest - Lowest + 1];
    private long _zero;
    private long _ticks;
    private long _min = long.MaxValue;
    private long _max;

    /// <summary>Records one time, in <see cref="Stopwatch"/> ticks. Only the module's own loop calls it.</summary>
    public void Record(long ticks)
    {
        if (ticks <= 0)
        {
            _zero++;
            ticks = 0;
        }
        else
        {
            _buckets[Math.Clamp(IndexOf(ticks * s_secondsPerTick), Lowest, Highest) - Lowest]++;
        }
        _ticks += ticks;
        if (ticks < _min)
            _min = ticks;
        if (ticks > _max)
            _max = ticks;
    }

    /// <summary>The times added up: how long the module has spent handling messages.</summary>
    public TimeSpan Total => Stopwatch.GetElapsedTime(0, Volatile.Read(ref _ticks));

    /// <summary>The histogram as it is now.</summary>
    public HandlingTimes Snapshot()
    {
        var first = -1;
        var last = -1;
        Span<long> counts = stackalloc long[_buckets.Length];
        long count = 0;
        for (var i = 0; i < _buckets.Length; i++)
        {
            counts[i] = Volatile.Read(ref _buckets[i]);
            if (counts[i] == 0)
                continue;
            count += counts[i];
            if (first < 0)
                first = i;
            last = i;
        }
        var zero = Volatile.Read(ref _zero);
        count += zero;
        if (count == 0)
            return HandlingTimes.Empty;
        var min = Volatile.Read(ref _min);
        return new HandlingTimes(
            count,
            Volatile.Read(ref _ticks) * s_secondsPerTick,
            min == long.MaxValue ? 0 : min * s_secondsPerTick,
            Volatile.Read(ref _max) * s_secondsPerTick,
            zero,
            Scale,
            first < 0 ? 0 : first + Lowest,
            first < 0 ? [] : counts[first..(last + 1)].ToArray());
    }

    /// <summary>
    /// The bucket a time in seconds falls in, as OpenTelemetry's exponential histograms map them: the doubling from the
    /// exponent's bits, and where in it from the mantissa, by comparison rather than a logarithm.
    /// </summary>
    internal static int IndexOf(double seconds)
    {
        var bits = BitConverter.DoubleToInt64Bits(seconds);
        var exponent = (int)((bits >> 52) & 0x7FF) - 1023;
        var mantissa = bits & 0xF_FFFF_FFFF_FFFFL;
        if (exponent == -1023)
            return Math.ILogB(seconds) << Scale; // subnormal: far below a nanosecond, so the lowest bucket anyway
        if (mantissa == 0)
            return (exponent << Scale) - 1; // an exact power of two is the top of the bucket below it
        var within = BitConverter.Int64BitsToDouble(mantissa | 0x3FF0_0000_0000_0000L); // from 1 to 2
        var step = 0;
        while (step < s_divisions.Length && within > s_divisions[step])
            step++;
        return (exponent << Scale) + step;
    }
}
