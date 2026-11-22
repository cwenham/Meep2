using Meep2.Runtime;
using Meep2.SDK.Modules;

namespace Meep2.Daemon.Subscriptions;

/// <summary>
/// Reads what changed in one pipeline for an update, against what was last sent for it: the update, the pipeline's
/// name, what's wanted of it and at which rate, its counts and its modules' keys, what was last sent, and the modules'
/// counts read for this update.
/// </summary>
internal sealed record PipelineRead(
    Update Update,
    string Name,
    PipelinePlan Wanted,
    TimeSpan Every,
    PipelineCounts Pipeline,
    IReadOnlyDictionary<ModuleInfo, string> Keys,
    PipelineSent Sent,
    ModuleReads Reads)
{
    /// <summary>The pipeline's part of the update.</summary>
    public PipelinePart Part => Update.Pipeline(Name);

    /// <summary>Whether a module's or link's <paramref name="kind"/> is wanted at this rate.</summary>
    public bool AtRate(UpdateSection kind, string key) => Wanted.RateOf(kind, key) == Every;

    /// <summary>Adds to the update what changed in the section asked for by <paramref name="slot"/>, at its rate.</summary>
    public void Read(Slot slot)
    {
        var atRate = this with { Every = slot.Every };
        switch (slot.What)
        {
            case UpdateSection.Totals:
                if (SentState.Changes(Reads.TotalsOf(Name, Pipeline), Sent.Totals) is { Count: > 0 } totals)
                    Part.Totals = totals;
                break;
            case UpdateSection.Modules:
                atRate.CountEach(UpdateSection.Modules, Sent.Modules, atRate.Keyed(), static (module, _) => module.Counts);
                break;
            case UpdateSection.Links:
                atRate.CountEach(UpdateSection.Links, Sent.Links, Pipeline.Links().Select(link => (link.Key, link)), static (link, _) => [link.Carried, link.Errors]);
                break;
            case UpdateSection.Bytes:
                // Only modules that talk to the network have any.
                atRate.CountEach(UpdateSection.Bytes, Sent.Bytes, atRate.Keyed(), static (module, sentBefore) =>
                    module.Traffic == default && !sentBefore ? null : [module.Traffic.Received, module.Traffic.Sent]);
                break;
            case UpdateSection.Timing:
                atRate.Timing();
                break;
            case UpdateSection.Arrivals or UpdateSection.Destinations:
                atRate.Places(slot.What);
                break;
        }
    }

    // Each module with a key, and its counts read for this update.
    private IEnumerable<(string Key, ModuleRead Module)> Keyed()
    {
        foreach (var module in Reads.Of(Pipeline))
        {
            if (Keys.TryGetValue(module.Module, out var key))
                yield return (key, module);
        }
    }

    // The counts of each module or link wanted at this rate, if they changed, and those sent before that have gone.
    // countsOf is given whether the key's counts were sent before, and returns null to send nothing for it yet.
    private void CountEach<T>(UpdateSection kind, Dictionary<string, long[]> sent, IEnumerable<(string Key, T Item)> items, Func<T, bool, long[]?> countsOf)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, item) in items)
        {
            if (!AtRate(kind, key))
                continue;
            seen.Add(key);
            if (countsOf(item, sent.ContainsKey(key)) is { } counts)
                Counts(kind, key, counts, sent);
        }
        Gone(kind, sent, seen);
    }

    private void Timing()
    {
        var sent = Sent.Timing;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, module) in Keyed())
        {
            if (!AtRate(UpdateSection.Timing, key))
                continue;
            seen.Add(key);
            // The busy time moves whenever the histogram does, and costs far less to read than the histogram.
            var busy = module.Busy.Ticks;
            if (sent.TryGetValue(key, out var before) ? before == busy : busy == 0 && module.Source)
                continue; // nothing new, or a source, which handles nothing
            var times = Pipeline.GetHandlingTimes(module.Module);
            sent[key] = busy;
            (Part.Timing ??= []).Add((key, times));
        }
        foreach (var key in sent.Keys.Where(k => !seen.Contains(k) && AtRate(UpdateSection.Timing, k)).ToList())
        {
            sent.Remove(key);
            (Part.Timing ??= []).Add((key, null));
        }
    }

    // A module's counts by place: where its messages arrive from, or where they go. Each place's counts go as an array,
    // as a module's do. A place past the module's bound is counted under * (or meep://*). A place folded there for
    // being quiet goes (null), and is named in the pipeline's folded, so a client moves its counts into *'s rather than
    // seeing *'s rate jump (Q215). For arrivals, the pipeline's clients names clients outside Meep, by address, once a
    // name is found (Q220); for destinations, its peers says which are Meep hosts, by ID.
    private void Places(UpdateSection kind)
    {
        var arrivals = kind == UpdateSection.Arrivals;
        var record = arrivals ? Sent.Arrivals : Sent.Destinations;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (module, places) in Pipeline.ReadPeers(arrivals))
        {
            if (!Keys.TryGetValue(module, out var key) || !AtRate(kind, key))
                continue;
            seen.Add(key);
            if (!record.TryGetValue(key, out var before))
                record[key] = before = new Dictionary<string, long[]>(StringComparer.Ordinal);
            var now = PlacesNow(places, before, arrivals);
            if (PlaceChanges(key, now, before) is { } changes)
                Part.AddPlaces(kind, key, changes);
        }
        foreach (var key in record.Keys.Where(k => !seen.Contains(k) && AtRate(kind, k)).ToList())
        {
            record.Remove(key);
            Part.AddPlaces(kind, key, null);
        }
    }

    // Each place's counts now, naming clients and peers as they're found.
    private Dictionary<string, long[]> PlacesNow(IReadOnlyList<PeerCount> places, Dictionary<string, long[]> before, bool arrivals)
    {
        var now = new Dictionary<string, long[]>(StringComparer.Ordinal);
        foreach (var place in places)
        {
            long[] counts = [place.Received, place.Sent, place.BytesReceived, place.BytesSent, place.Failed, place.Rejected];
            // A place counted with nothing yet, as a WebSocket's client before its first message, isn't shown.
            if (!before.ContainsKey(place.Place) && counts.All(c => c == 0))
                continue;
            now[place.Place] = counts;
            if (arrivals)
                Client(place.Place);
            else
                Peer(place.Place, place.Meep);
        }
        return now;
    }

    // The places whose counts changed since they were last sent, and those folded away, with before brought up to now.
    private List<CountChange>? PlaceChanges(string key, Dictionary<string, long[]> now, Dictionary<string, long[]> before)
    {
        List<CountChange>? changes = null;
        foreach (var (place, counts) in now)
        {
            var last = before.GetValueOrDefault(place);
            if (last is not null && counts.AsSpan().SequenceEqual(last))
                continue;
            if (last is not null && counts.Where((count, i) => count < last[i]).Any())
            {
                // Folded and back again since the last update: it starts again from what it has now.
                Part.Fold(key, place);
                last = null;
            }
            (changes ??= []).Add(new CountChange(place, counts, last));
            before[place] = counts;
        }
        foreach (var place in before.Keys.Where(p => !now.ContainsKey(p)).ToList())
        {
            // Places go only by being folded into * (or meep://*).
            before.Remove(place);
            (changes ??= []).Add(new CountChange(place, null, null));
            Part.Fold(key, place);
        }
        return changes;
    }

    // Names a client outside Meep by its address, when a name is first found for it or changes.
    private void Client(string place)
    {
        if (ClientNames.NameOf(place) is not { } found || (Sent.Clients.TryGetValue(place, out var told) && told == found))
            return;
        Sent.Clients[place] = found;
        (Part.Clients ??= []).Add((place, found));
    }

    // Says which Meep host a destination is, when that's new or has changed.
    private void Peer(string endpoint, string? id)
    {
        if (Sent.Peers.TryGetValue(endpoint, out var told) ? told == id : id is null)
            return;
        if (id is null)
            Sent.Peers.Remove(endpoint);
        else
            Sent.Peers[endpoint] = id;
        (Part.Peers ??= []).Add((endpoint, id));
    }

    // A module's or link's counts, if any changed since they were last sent.
    private void Counts(UpdateSection kind, string key, long[] now, Dictionary<string, long[]> sent)
    {
        if (sent.TryGetValue(key, out var before))
        {
            if (now.AsSpan().SequenceEqual(before))
                return;
            Part.Add(kind, key, now, before);
        }
        else
        {
            Part.Add(kind, key, now, null);
        }
        sent[key] = now;
    }

    // Modules or links that were sent at this rate and aren't there now, as when an edit removed them.
    private void Gone(UpdateSection kind, Dictionary<string, long[]> sent, HashSet<string> seen)
    {
        foreach (var key in sent.Keys.Where(k => !seen.Contains(k) && AtRate(kind, k)).ToList())
        {
            sent.Remove(key);
            Part.Add(kind, key, null, null);
        }
    }
}
