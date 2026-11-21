using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Meep2.SDK.Modules;

namespace Meep2.Daemon.Subscriptions;

/// <summary>
/// Names for the addresses of clients outside Meep, which a Listen's arrivals are keyed by, given beside them in a
/// subscription's <c>clients</c> (Q220). Names are looked up in the background, by the address's reverse DNS entry,
/// within two seconds, and kept for ten minutes. When the table is full, names older than that are dropped to make room
/// (M3), at most every ten seconds, so a public Listen's many clients don't keep later ones from being named.
/// </summary>
internal static class ClientNames
{
    private const int Limit = 4096;
    private static readonly TimeSpan s_keep = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_sweepEvery = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, Entry> s_names = new(StringComparer.Ordinal);
    private static long s_swept = long.MinValue;

    /// <summary>The clock, in milliseconds; tests replace it.</summary>
    internal static Func<long> Now { get; set; } = () => Environment.TickCount64;

    /// <summary>How many addresses are remembered.</summary>
    internal static int Count => s_names.Count;

    /// <summary>Whether an address is remembered, named or not.</summary>
    internal static bool Knows(string place) => s_names.ContainsKey(place);

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
        var now = Now();
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
        if (s_names.Count >= Limit)
            Sweep(now);
        if (s_names.Count < Limit && s_names.TryAdd(place, new Entry(null, now)))
            _ = LookUpAsync(place, address);
        return null;
    }

    /// <summary>Remembers a name as though it had been looked up at a time, for tests.</summary>
    internal static void Remember(string place, string? name, long at) => s_names[place] = new Entry(name, at);

    /// <summary>Forgets every name, for tests.</summary>
    internal static void Clear()
    {
        s_names.Clear();
        Interlocked.Exchange(ref s_swept, long.MinValue);
    }

    // Drops names kept longer than they're kept for, at most every ten seconds.
    private static void Sweep(long now)
    {
        var last = Interlocked.Read(ref s_swept);
        if ((last != long.MinValue && now - last < s_sweepEvery.TotalMilliseconds) || Interlocked.CompareExchange(ref s_swept, now, last) != last)
            return;
        foreach (var (place, entry) in s_names)
        {
            if (now - entry.At >= s_keep.TotalMilliseconds)
                s_names.TryRemove(new KeyValuePair<string, Entry>(place, entry));
        }
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
        s_names[place] = new Entry(name, Now());
    }

    private sealed record Entry(string? Name, long At);
}
