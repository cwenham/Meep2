using Meep2.SDK.Modules;
using Meep2.Runtime;

namespace Meep2.Daemon.Subscriptions;

/// <summary>One module's counts, read once for an update, so its totals and its modules agree.</summary>
internal readonly record struct ModuleRead(ModuleInfo Module, long[] Counts, ModuleTraffic Traffic, TimeSpan Busy, bool Source)
{
    /// <summary>A module's reading, with its counts in the order of <see cref="Subscription.ModuleCounts"/>.</summary>
    public static ModuleRead Of(ModuleReading reading)
    {
        var (module, stats, traffic, busy, source) = reading;
        return new ModuleRead(
            module,
            [
                stats.Received, stats.Handled, stats.Emitted, stats.Passed, (long)busy.TotalMicroseconds, stats.Queued,
                stats.Dropped, stats.Filtered, stats.Failed, stats.Restarts, stats.Expired, stats.Refused,
            ],
            traffic,
            busy,
            source);
    }
}
