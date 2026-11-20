using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Meep2.SDK.Modules;

namespace Meep2.Daemon.Subscriptions;

/// <summary>
/// Names for the addresses of clients outside Meep, which a Listen's arrivals are keyed by, given beside them in a
/// subscription's <c>clients</c> (Q220). Names are looked up in the background, by the address's reverse DNS entry,
/// within two seconds, and kept for ten minutes.
/// </summary>
internal static class ClientNames
{
    private const int Limit = 4096;
    private static readonly TimeSpan s_keep = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(2);
    private static readonly ConcurrentDictionary<string, Entry> s_names = new(StringComparer.Ordinal);

    /// <summary>
    /// The name found for a client's address, or null while there's none: for an origin in Meep or <c>*</c>, an address
    /// with no name, or one still being looked up. Loopback addresses are <c>localhost</c>.
    /// </summary>
    public static string? NameOf(string place)
    {
        if (place.StartsWith(Peers.MeepScheme, StringComparison.Ordinal) || !IPAddress.TryParse(place, out var address))
            return null;
        if (IPAddress.IsLoopback(address))
            return "localhost";
        var now = Environment.TickCount64;
        if (s_names.TryGetValue(place, out var entry))
        {
            if (now - entry.At >= s_keep.TotalMilliseconds)
            {
                // Kept too long: look again, showing the name found before until then.
                s_names[place] = entry with { At = now };
                _ = LookUpAsync(place, address);
            }
            return entry.Name;
        }
        if (s_names.Count < Limit && s_names.TryAdd(place, new Entry(null, now)))
            _ = LookUpAsync(place, address);
        return null;
    }

    private static async Task LookUpAsync(string place, IPAddress address)
    {
        string? name = null;
        try
        {
            using var timeout = new CancellationTokenSource(s_wait);
            var found = await Dns.GetHostEntryAsync(address.ToString(), timeout.Token).ConfigureAwait(false);
            if (found.HostName.Length > 0 && !IPAddress.TryParse(found.HostName, out _))
                name = found.HostName.TrimEnd('.').ToLowerInvariant();
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or ArgumentException)
        {
            // No name, or none in time: shown by its address.
        }
        s_names[place] = new Entry(name, Environment.TickCount64);
    }

    private sealed record Entry(string? Name, long At);
}
