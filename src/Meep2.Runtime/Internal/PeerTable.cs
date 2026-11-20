using System.Collections.Concurrent;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>
/// One module's counts by place at the other end of its connections: where messages arrive from, or where they go
/// (<see cref="ModuleContext.Arrival"/>, <see cref="ModuleContext.Destination"/>). It keeps a bounded number of places.
/// When a new one needs room, the place quiet for longest, if it has been quiet for <see cref="Quiet"/>, is folded into
/// <c>*</c>, or <c>meep://*</c> for origins in Meep: its counts are added there and it goes. With none that quiet, the
/// new place is counted there instead. Either way the counts still add up to the module's whole.
/// </summary>
/// <remarks>
/// Folding happens only when a new place needs the room, never on a timer (Q215). A tally kept after its place was
/// folded, such as a WebSocket connection's, counts into where it was folded.
/// </remarks>
internal sealed class PeerTable(int meepLimit, int otherLimit)
{
    /// <summary>Where places past the bound are counted.</summary>
    public const string Others = "*";

    /// <summary>Where origins in Meep past the bound are counted.</summary>
    public const string OtherMeep = Peers.MeepScheme + "*";

    /// <summary>How long a place has to have been quiet to be folded for a new one.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);

    /// <summary>The clock places are timed by, in milliseconds: <see cref="Environment.TickCount64"/>, or a test's.</summary>
    internal static Func<long> Clock { get; set; } = static () => Environment.TickCount64;

    /// <summary>How many origins in Meep a Listen keeps, and how many other clients.</summary>
    public static PeerTable ForArrivals() => new(256, 64);

    /// <summary>How many endpoints a Fetch or Send keeps.</summary>
    public static PeerTable ForDestinations() => new(64, 64);

    private readonly ConcurrentDictionary<string, Counts> _places = new(StringComparer.Ordinal);
    private readonly Lock _making = new();
    private int _meep;
    private int _other;

    /// <summary>The counts for a place: its own, or <c>*</c>'s when there's no room for it.</summary>
    public Tally For(string place)
    {
        ArgumentNullException.ThrowIfNull(place);
        if (_places.TryGetValue(place, out var counts))
            return counts;
        lock (_making)
        {
            if (_places.TryGetValue(place, out counts))
                return counts;
            var meep = place.StartsWith(Peers.MeepScheme, StringComparison.Ordinal);
            ref var taken = ref meep ? ref _meep : ref _other;
            if (place is Others or OtherMeep)
                return Overflow(meep);
            if (taken >= (meep ? meepLimit : otherLimit) && !FoldQuietest(meep))
                return Overflow(meep);
            counts = new Counts();
            _places[place] = counts;
            taken++;
            return counts;
        }
    }

    /// <summary>Every place's counts now, in no particular order. A folded place isn't among them.</summary>
    public IReadOnlyList<PeerCount> Read()
    {
        var read = new List<PeerCount>(_places.Count);
        foreach (var (place, counts) in _places)
            read.Add(counts.Read(place));
        return read;
    }

    private Counts Overflow(bool meep) => _places.GetOrAdd(meep ? OtherMeep : Others, static _ => new Counts());

    // Folds the place of a kind that has been quiet longest, if it has been quiet long enough. Under _making.
    private bool FoldQuietest(bool meep)
    {
        var before = Clock() - (long)Quiet.TotalMilliseconds;
        KeyValuePair<string, Counts>? quietest = null;
        foreach (var entry in _places)
        {
            if (entry.Key is Others or OtherMeep || entry.Key.StartsWith(Peers.MeepScheme, StringComparison.Ordinal) != meep)
                continue;
            var last = entry.Value.LastActive;
            if (last <= before && (quietest is null || last < quietest.Value.Value.LastActive))
                quietest = entry;
        }
        if (quietest is not { } fold)
            return false;
        _places.TryRemove(fold);
        fold.Value.FoldInto(Overflow(meep));
        if (meep)
            _meep--;
        else
            _other--;
        return true;
    }

    private sealed class Counts : Tally
    {
        private long _received;
        private long _sent;
        private long _bytesReceived;
        private long _bytesSent;
        private long _failed;
        private long _rejected;
        private long _last = Clock();
        private volatile Counts? _into;
        private volatile string? _meep;

        /// <summary>When it was last counted, by <see cref="Clock"/>.</summary>
        public long LastActive => Volatile.Read(ref _last);

        public override void Count(long received = 0, long sent = 0, long bytesReceived = 0, long bytesSent = 0, long failed = 0, long rejected = 0)
        {
            if (_into is { } into)
            {
                into.Count(received, sent, bytesReceived, bytesSent, failed, rejected);
                return;
            }
            if (received != 0)
                Interlocked.Add(ref _received, received);
            if (sent != 0)
                Interlocked.Add(ref _sent, sent);
            if (bytesReceived != 0)
                Interlocked.Add(ref _bytesReceived, bytesReceived);
            if (bytesSent != 0)
                Interlocked.Add(ref _bytesSent, bytesSent);
            if (failed != 0)
                Interlocked.Add(ref _failed, failed);
            if (rejected != 0)
                Interlocked.Add(ref _rejected, rejected);
            Volatile.Write(ref _last, Clock());
        }

        public override void Meep(string? hostId) => _meep = hostId;

        // From now on it counts into another, and what it has goes there.
        public void FoldInto(Counts into)
        {
            _into = into;
            into.Count(
                Interlocked.Exchange(ref _received, 0),
                Interlocked.Exchange(ref _sent, 0),
                Interlocked.Exchange(ref _bytesReceived, 0),
                Interlocked.Exchange(ref _bytesSent, 0),
                Interlocked.Exchange(ref _failed, 0),
                Interlocked.Exchange(ref _rejected, 0));
        }

        public PeerCount Read(string place) => new(
            place,
            Volatile.Read(ref _received),
            Volatile.Read(ref _sent),
            Volatile.Read(ref _bytesReceived),
            Volatile.Read(ref _bytesSent),
            Volatile.Read(ref _failed),
            _meep)
        {
            Rejected = Volatile.Read(ref _rejected),
        };
    }
}
