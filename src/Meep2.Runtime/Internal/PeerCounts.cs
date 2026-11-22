using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>
/// One module's places at the other end of its connections: where it listens, counts by where its messages arrive
/// from and where they go, made once it counts any, and the origin it gives other Meep hosts.
/// </summary>
internal sealed class PeerCounts
{
    private PeerTable? _arrivals;
    private PeerTable? _destinations;
    private volatile OriginMade? _origin;
    private volatile string? _listening;

    /// <summary>Where the module says it's listening, or null (<see cref="ModuleContext.Listening"/>).</summary>
    public string? ListeningAt
    {
        get => _listening;
        set => _listening = value;
    }

    /// <summary>Counts by where messages arrive from, once the module has counted any (<see cref="ModuleContext.Arrival"/>).</summary>
    public PeerTable? Arrivals => Volatile.Read(ref _arrivals);

    /// <summary>Counts by where messages go, once the module has counted any (<see cref="ModuleContext.Destination"/>).</summary>
    public PeerTable? Destinations => Volatile.Read(ref _destinations);

    public Tally ArrivalFrom(string from) =>
        (Volatile.Read(ref _arrivals) ?? Made(ref _arrivals, PeerTable.ForArrivals())).For(from);

    public Tally DestinationAt(string endpoint) =>
        (Volatile.Read(ref _destinations) ?? Made(ref _destinations, PeerTable.ForDestinations())).For(endpoint);

    /// <summary>
    /// Where messages the module sends come from, for another Meep host (<see cref="ModuleContext.Origin"/>): made once
    /// for each host ID and key, and null without either.
    /// </summary>
    public string? OriginOf(string? hostId, string pipeline, string? key)
    {
        if (hostId is null || key is null)
            return null;
        if (_origin is { } made && ReferenceEquals(made.HostId, hostId) && ReferenceEquals(made.Key, key))
            return made.Origin;
        var origin = Peers.Origin(hostId, pipeline, key);
        _origin = new OriginMade(hostId, key, origin);
        return origin;
    }

    private static PeerTable Made(ref PeerTable? field, PeerTable made) =>
        Interlocked.CompareExchange(ref field, made, null) ?? made;

    private sealed record OriginMade(string HostId, string Key, string Origin);
}
