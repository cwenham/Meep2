using System.Globalization;
using System.Text;

namespace Meep2.SDK.Modules;

/// <summary>Durations written as Meeplang writes them, such as 250ms, 30s or 1h30m, for messages and pipeline text.</summary>
public static class DurationText
{
    /// <summary>
    /// Writes a duration as Meeplang reads it: in whole days, hours, minutes, seconds and milliseconds, such as 1m40s,
    /// or, for one with a part of a millisecond, in the largest unit that fits, such as 1.5ms or 2.25s.
    /// </summary>
    public static string Write(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            return "-" + Write(duration.Negate());
        if (duration == TimeSpan.Zero)
            return "0s";
        if (duration.Ticks % TimeSpan.TicksPerMillisecond != 0)
            return Approximately(duration);

        var text = new StringBuilder();
        Part(text, (long)duration.TotalDays, "d");
        Part(text, duration.Hours, "h");
        Part(text, duration.Minutes, "m");
        Part(text, duration.Seconds, "s");
        Part(text, duration.Milliseconds, "ms");
        return text.ToString();
    }

    private static void Part(StringBuilder text, long amount, string unit)
    {
        if (amount > 0)
            text.Append(amount.ToString(CultureInfo.InvariantCulture)).Append(unit);
    }

    private static string Approximately(TimeSpan duration) =>
        duration.TotalMilliseconds < 1000 ? Number(duration.TotalMilliseconds) + "ms"
        : duration.TotalSeconds < 60 ? Number(duration.TotalSeconds) + "s"
        : duration.TotalMinutes < 60 ? Number(duration.TotalMinutes) + "m"
        : Number(duration.TotalHours) + "h";

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
