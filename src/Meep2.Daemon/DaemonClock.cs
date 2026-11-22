namespace Meep2.Daemon;

/// <summary>The daemon's clock, as it gives it to clients: microseconds since 1970 in UTC.</summary>
public static class DaemonClock
{
    /// <summary>The time now.</summary>
    public static long Now => Microseconds(DateTimeOffset.UtcNow);

    /// <summary>A time as microseconds since 1970 in UTC.</summary>
    public static long Microseconds(DateTimeOffset time) => (time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;
}
