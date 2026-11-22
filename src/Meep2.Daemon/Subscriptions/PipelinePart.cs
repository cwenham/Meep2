using Meep2.Runtime;

namespace Meep2.Daemon.Subscriptions;

/// <summary>What changed for one pipeline in an update, by section, and how it's written.</summary>
internal sealed class PipelinePart
{
    public bool Gone { get; set; }

    public (int Version, string? Reason)? Graph { get; set; }

    public List<NamedCount>? Totals { get; set; }

    public Dictionary<UpdateSection, List<CountChange>> Counts { get; } = [];

    public List<(string Key, HandlingTimes? Times)>? Timing { get; set; }

    public Dictionary<UpdateSection, List<(string Key, List<CountChange>? Places)>> Places { get; } = [];

    public List<(string Endpoint, string? Id)>? Peers { get; set; }

    public List<(string Address, string Name)>? Clients { get; set; }

    public Dictionary<string, List<string>>? Folded { get; set; }

    public void Fold(string key, string place)
    {
        Folded ??= new(StringComparer.Ordinal);
        if (!Folded.TryGetValue(key, out var places))
            Folded[key] = places = [];
        places.Add(place);
    }

    public void AddPlaces(UpdateSection kind, string key, List<CountChange>? places)
    {
        if (!Places.TryGetValue(kind, out var list))
            Places[kind] = list = [];
        list.Add((key, places));
    }

    public void Add(UpdateSection kind, string key, long[]? now, long[]? before)
    {
        if (!Counts.TryGetValue(kind, out var list))
            Counts[kind] = list = [];
        list.Add(new CountChange(key, now, before));
    }

    public int Size =>
        (Graph is null ? 0 : 1) + (Totals is null ? 0 : 1) + Counts.Count + (Timing is null ? 0 : 1) + Places.Count + (Peers is null ? 0 : 1) + (Clients is null ? 0 : 1) + (Folded is null ? 0 : 1);

    /// <summary>Writes the part as a map of its sections, or null for a pipeline that's gone.</summary>
    public void Write(UpdateWriter writer)
    {
        if (Gone)
        {
            writer.Null();
            return;
        }
        writer.StartMap(Size);
        WriteGraph(writer);
        if (Totals is { } totals)
        {
            writer.Name(SubscriptionPlan.NameOf(UpdateSection.Totals));
            writer.Named(totals);
        }
        foreach (var (kind, counts) in Counts)
        {
            writer.Name(SubscriptionPlan.NameOf(kind));
            writer.Changes(counts);
        }
        WritePlaces(writer);
        WriteFolded(writer);
        WriteClients(writer);
        WritePeers(writer);
        WriteTiming(writer);
        writer.EndMap();
    }

    private void WriteGraph(UpdateWriter writer)
    {
        if (Graph is not { } graph)
            return;
        writer.Name("graph");
        writer.StartMap(graph.Reason is null ? 1 : 2);
        writer.Property("version", graph.Version);
        if (graph.Reason is { } reason)
            writer.Property("reason", reason);
        writer.EndMap();
    }

    private void WritePlaces(UpdateWriter writer)
    {
        foreach (var (kind, modules) in Places)
        {
            writer.Name(SubscriptionPlan.NameOf(kind));
            writer.StartMap(modules.Count);
            foreach (var (key, places) in modules)
            {
                writer.Name(key);
                if (places is null)
                    writer.Null();
                else
                    writer.Changes(places);
            }
            writer.EndMap();
        }
    }

    private void WriteFolded(UpdateWriter writer)
    {
        if (Folded is not { } folded)
            return;
        writer.Name("folded");
        writer.StartMap(folded.Count);
        foreach (var (key, places) in folded)
        {
            writer.Name(key);
            writer.StartArray(places.Count);
            foreach (var place in places)
                writer.String(place);
            writer.EndArray();
        }
        writer.EndMap();
    }

    private void WriteClients(UpdateWriter writer)
    {
        if (Clients is not { } clients)
            return;
        writer.Name("clients");
        writer.StartMap(clients.Count);
        foreach (var (address, client) in clients)
            writer.Property(address, client);
        writer.EndMap();
    }

    private void WritePeers(UpdateWriter writer)
    {
        if (Peers is not { } peers)
            return;
        writer.Name("peers");
        writer.StartMap(peers.Count);
        foreach (var (endpoint, id) in peers)
        {
            writer.Name(endpoint);
            if (id is null)
                writer.Null();
            else
                writer.String(id);
        }
        writer.EndMap();
    }

    private void WriteTiming(UpdateWriter writer)
    {
        if (Timing is not { } timing)
            return;
        writer.Name(SubscriptionPlan.NameOf(UpdateSection.Timing));
        writer.StartMap(timing.Count);
        foreach (var (key, times) in timing)
        {
            writer.Name(key);
            if (times is null)
                writer.Null();
            else
                WriteTimes(writer, times);
        }
        writer.EndMap();
    }

    private static void WriteTimes(UpdateWriter writer, HandlingTimes times)
    {
        writer.StartMap(7);
        writer.Property("count", times.Count);
        writer.Property("sum", times.Sum);
        writer.Property("min", times.Min);
        writer.Property("max", times.Max);
        writer.Property("zero", times.Zero);
        writer.Property("offset", (long)times.Offset);
        writer.Name("buckets");
        writer.StartArray(times.Buckets.Count);
        foreach (var count in times.Buckets)
            writer.Number(count);
        writer.EndArray();
        writer.EndMap();
    }
}
