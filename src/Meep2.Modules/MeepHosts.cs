using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// The hosts and ports this process knows to be Meep, so Fetch and Send give origins only to them (<see cref="Peers"/>):
/// those a setting names (<see cref="Configure"/>), those that have answered <c>Meep-Host</c> with their ID, here or
/// before a restart (<see cref="Keep"/>), and this process's own Listens.
/// </summary>
/// <remarks>
/// <para>
/// A request to a host and port not known yet asks, and carries no origin, so the first request from this process to
/// a Meep host neither named nor remembered counts there as from outside. A host that has answered is forgotten, here
/// and in the saved list, when it answers a request that carried an origin without saying <c>Meep-Host</c>. A host
/// the setting names stays known whatever it answers.
/// </para>
/// <para>
/// Only hosts the setting names, this machine, and hosts on private addresses are asked or told at all (Q224, S11), so
/// an API on the internet learns nothing about the pipelines that call it. A Listen answers <c>Meep-Host</c> only to
/// clients on this machine and private addresses, unless <see cref="AnswerAnyone"/> is set.
/// </para>
/// </remarks>
public static class MeepHosts
{
    /// <summary>The most hosts and ports remembered, so a Fetch with addresses worked out for each message can't grow it without end.</summary>
    private const int Limit = 4096;

    private static readonly ConcurrentDictionary<string, string> s_known = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> s_configured = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<int, string> s_ownPorts = new();
    private static readonly ConcurrentDictionary<string, byte> s_files = new(StringComparer.Ordinal);
    private static readonly Lock s_saving = new();
    private static readonly Lock s_addressing = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, string> s_baggage = new();
    private static readonly ConcurrentDictionary<string, (long At, bool Private)> s_privateNames = new(StringComparer.OrdinalIgnoreCase);
    private static (long At, HashSet<IPAddress> Addresses) s_ownAddresses = (long.MinValue, []);
    private static Timer? s_saveTimer;
    private static volatile bool s_answerAnyone;

    /// <summary>
    /// Whether Listens answer <c>Meep-Host</c> with this host's ID to clients anywhere, as the daemon's
    /// <c>meep-host-public</c> setting says, rather than only to clients on this machine and private addresses (Q224).
    /// </summary>
    public static bool AnswerAnyone
    {
        get => s_answerAnyone;
        set => s_answerAnyone = value;
    }

    /// <summary>What a request told the other end.</summary>
    internal enum Marked
    {
        /// <summary>Nothing: not from a pipeline, or no host ID to answer with.</summary>
        Nothing,

        /// <summary>It asked whether the other end is Meep.</summary>
        Asked,

        /// <summary>It gave its origin, to a host known to be Meep.</summary>
        Origin,
    }

    /// <summary>
    /// Names hosts known to be Meep, as the daemon's <c>meep-hosts</c> setting does (Q213): each a host, for any port
    /// on it, or a host and port, as in <c>studio.local</c>, <c>10.0.0.5:8080</c> or <c>[fd00::5]:8080</c>. Requests
    /// to them carry origins from the first.
    /// </summary>
    public static void Configure(IEnumerable<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        foreach (var host in hosts)
        {
            if (Named(host) is { } named)
                s_configured[named] = 0;
        }
    }

    /// <summary>
    /// A host as <see cref="Configure"/> takes it, as it's matched: a host and port as destinations are keyed, or a
    /// host alone in lower case; null for one that isn't either.
    /// </summary>
    public static string? Named(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var text = host.Trim().ToLowerInvariant();
        if (text.Length == 0 || text.Contains('/', StringComparison.Ordinal) || text.Contains(' ', StringComparison.Ordinal))
            return null;
        if (text.StartsWith('['))
        {
            // An IPv6 address, with its port after the bracket if it has one.
            var close = text.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
                return null;
            var address = text[1..close];
            return close == text.Length - 1 ? address
                : text[close + 1] == ':' && Port(text[(close + 2)..]) is { } v6Port ? Peers.Endpoint(address, v6Port)
                : null;
        }
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0 || text.IndexOf(':', colon + 1) >= 0)
            return text; // a name, or an IPv6 address without brackets
        return Port(text[(colon + 1)..]) is { } port && colon > 0 ? Peers.Endpoint(text[..colon], port) : null;

        static int? Port(string text) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536 ? port : null;
    }

    /// <summary>
    /// Remembers the Meep hosts learned in a file, such as <c>~/.meep/daemon/meep-hosts</c>, across restarts: reads
    /// those it has now, and writes it again, a second after any change, until disposed. A line is a host and port, a
    /// space, and its ID.
    /// </summary>
    public static IDisposable Keep(string file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var full = Path.GetFullPath(file);
        try
        {
            foreach (var line in File.Exists(full) ? File.ReadAllLines(full) : [])
            {
                var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
                if (parts is [var endpoint, var id] && Named(endpoint) == endpoint && endpoint.Contains(':', StringComparison.Ordinal)
                    && id.Length is > 0 and <= 64 && s_known.Count < Limit)
                    s_known.TryAdd(endpoint, id);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unreadable: learned again as requests are answered.
        }
        s_files[full] = 0;
        return new Kept(full);
    }

    /// <summary>The host ID of the Meep at a host and port, if it has said, or it's this process's own Listen.</summary>
    internal static string? IdOf(string? endpoint, string host, int port)
    {
        if (endpoint is null)
            return null;
        if (s_known.TryGetValue(endpoint, out var id))
            return id;
        return s_ownPorts.TryGetValue(port, out var own) && IsThisMachine(host) ? own : null;
    }

    /// <summary>Whether a host and port is known to be Meep: it has said so, a setting names it, or it's this process's own.</summary>
    internal static bool IsMeep(string? endpoint, string host, int port) =>
        endpoint is not null
        && (IdOf(endpoint, host, port) is not null
            || (!s_configured.IsEmpty && (s_configured.ContainsKey(endpoint) || s_configured.ContainsKey(host.Trim('[', ']').ToLowerInvariant()))));

    /// <summary>
    /// Whether a host may be asked whether it's Meep, or given an origin (S11): one the setting names, this machine, or
    /// a host on a private address. A name is looked up, and private only if every address it has is.
    /// </summary>
    internal static bool MayTell(string? endpoint, string host)
    {
        if (endpoint is null)
            return false;
        var name = host.Trim('[', ']');
        if (!s_configured.IsEmpty && (s_configured.ContainsKey(endpoint) || s_configured.ContainsKey(name.ToLowerInvariant())))
            return true;
        return IsThisMachine(name) || IsPrivateName(name);
    }

    /// <summary>Whether a Listen answers a client at this address with this host's ID (S11).</summary>
    internal static bool MayAnswer(IPAddress? client) =>
        s_answerAnyone || client is null || IsPrivate(client) || OwnAddresses().Contains(client.IsIPv4MappedToIPv6 ? client.MapToIPv4() : client);

    /// <summary>
    /// Whether an address is on this machine or a private network: loopback, the private IPv4 ranges (10/8,
    /// 172.16/12, 192.168/16), link-local, and IPv6's unique-local and link-local ranges. Not the shared range carriers
    /// use (100.64/10), where others' machines may be (Q231): Tailscale's machines, which are given addresses there,
    /// are named in <c>meep-hosts</c> instead.
    /// </summary>
    internal static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            address.TryWriteBytes(b, out _);
            return b[0] == 10
                || (b[0] == 172 && (b[1] & 0xF0) == 16)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }
        return address.IsIPv6UniqueLocal || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
    }

    // A host name or address is private if it's an address that is, or a name whose addresses all are. Names are
    // looked up again at most every 30 seconds.
    private static bool IsPrivateName(string name)
    {
        if (IPAddress.TryParse(name, out var address))
            return IsPrivate(address);
        var now = Environment.TickCount64;
        if (s_privateNames.TryGetValue(name, out var known) && now - known.At < 30_000)
            return known.Private;
        bool isPrivate;
        try
        {
            var addresses = Dns.GetHostAddresses(name);
            isPrivate = addresses.Length > 0 && addresses.All(IsPrivate);
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or ArgumentException)
        {
            isPrivate = false;
        }
        if (s_privateNames.Count < Limit || s_privateNames.ContainsKey(name))
            s_privateNames[name] = (now, isPrivate);
        return isPrivate;
    }

    /// <summary>
    /// Adds what a request to another host says about where it comes from: <c>traceparent</c> while tracing, and the
    /// origin to a Meep host, or the question to anywhere not known to be one, among the hosts it may tell
    /// (<see cref="MayTell"/>). With <paramref name="ask"/>, it only
    /// asks, wherever it goes, as to the host a redirect leads to (Q217).
    /// </summary>
    internal static Marked Mark(HttpHeaders headers, string? endpoint, Uri uri, ModuleContext context, bool ask = false)
    {
        if (context.TraceParent is { } traceParent)
            headers.TryAddWithoutValidation("traceparent", traceParent);
        if (endpoint is null || context.HostId is null || !MayTell(endpoint, uri.Host))
            return Marked.Nothing;
        if (!ask && IsMeep(endpoint, uri.Host, uri.Port) && context.Origin is { } origin)
        {
            headers.TryAddWithoutValidation("baggage", BaggageOf(origin));
            return Marked.Origin;
        }
        headers.TryAddWithoutValidation(Peers.HostHeader, Peers.Ask);
        return Marked.Asked;
    }

    /// <summary>The baggage for an origin, made once for each module.</summary>
    internal static string BaggageOf(string origin) => s_baggage.GetValue(origin, Peers.Baggage);

    /// <summary>
    /// Takes in what the other end answered a marked request with: remembers a Meep host, and forgets one that no
    /// longer says it is. Returns the host ID, or null if it isn't known.
    /// </summary>
    internal static string? Answered(string? endpoint, HttpHeaders? answer, Marked marked) =>
        Answered(endpoint, answer is not null && answer.NonValidated.TryGetValues(Peers.HostHeader, out var values) && values.Count > 0 ? values.ToString() : null, marked);

    /// <inheritdoc cref="Answered(string?, HttpHeaders?, Marked)"/>
    internal static string? Answered(string? endpoint, string? answer, Marked marked)
    {
        if (endpoint is null || marked == Marked.Nothing)
            return endpoint is null ? null : s_known.GetValueOrDefault(endpoint);
        var id = answer is { Length: > 0 and <= 64 } && answer != Peers.Ask && !answer.Contains(' ', StringComparison.Ordinal) ? answer : null;
        if (id is null)
        {
            if (s_known.TryRemove(endpoint, out _))
                Changed();
        }
        else if ((s_known.Count < Limit || s_known.ContainsKey(endpoint)) && s_known.GetValueOrDefault(endpoint) != id)
        {
            s_known[endpoint] = id;
            Changed();
        }
        return id;
    }

    /// <summary>Remembers that a Listen of this process, in the host <paramref name="hostId"/>, is bound to a port.</summary>
    internal static void Bound(int port, string? hostId)
    {
        if (hostId is not null)
            s_ownPorts[port] = hostId;
    }

    /// <summary>Forgets a port this process's Listen has let go of.</summary>
    internal static void Unbound(int port, string? hostId)
    {
        if (hostId is not null)
            s_ownPorts.TryRemove(new KeyValuePair<int, string>(port, hostId));
    }

    /// <summary>Forgets one host learned, for tests, as though it had never answered, without saving.</summary>
    internal static void Forget(string endpoint) => s_known.TryRemove(endpoint, out _);

    /// <summary>Writes the files now, rather than after the second's wait.</summary>
    internal static void Save()
    {
        lock (s_saving)
        {
            s_saveTimer?.Dispose();
            s_saveTimer = null;
            // A snapshot first: sorting the dictionary itself while requests change it can read past its end.
            var lines = s_known.ToArray().OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}").ToList();
            foreach (var file in s_files.Keys)
            {
                try
                {
                    var temporary = file + ".new";
                    File.WriteAllLines(temporary, lines);
                    File.Move(temporary, file, overwrite: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Not saved this time: what's known is still used, and saved with the next change.
                }
            }
        }
    }

    // A change to what's learned is saved a second later, with any others in that second.
    private static void Changed()
    {
        if (s_files.IsEmpty)
            return;
        lock (s_saving)
            s_saveTimer ??= new Timer(_ => Save(), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    // Whether a host names this machine: a loopback address or name, its own name, or one of its addresses.
    private static bool IsThisMachine(string host)
    {
        var name = host.Trim('[', ']');
        if (IPAddress.TryParse(name, out var address))
            return IPAddress.IsLoopback(address) || OwnAddresses().Contains(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        var machine = Environment.MachineName;
        return name.Equals(machine, StringComparison.OrdinalIgnoreCase)
            || name.Equals(machine + ".local", StringComparison.OrdinalIgnoreCase)
            || name.Equals(Dns.GetHostName(), StringComparison.OrdinalIgnoreCase);
    }

    // The machine's addresses, looked up again at most every 30 seconds.
    private static HashSet<IPAddress> OwnAddresses()
    {
        lock (s_addressing)
        {
            var now = Environment.TickCount64;
            if (now - s_ownAddresses.At < 30_000)
                return s_ownAddresses.Addresses;
            var addresses = new HashSet<IPAddress>();
            try
            {
                foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                        addresses.Add(unicast.Address);
                }
            }
            catch (NetworkInformationException)
            {
                // None to compare with.
            }
            s_ownAddresses = (now, addresses);
            return addresses;
        }
    }

    // Stops saving to a file, saving it once more first.
    private sealed class Kept(string file) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Save();
            s_files.TryRemove(file, out _);
        }
    }
}
