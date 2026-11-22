using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meep2.Daemon.Subscriptions;

/// <summary>
/// Writes a subscription's events, as JSON or as CBOR (Q202), from the same code, so both carry the same values in the
/// same shape. Maps and arrays are given their length when it's known, which CBOR writes in their first byte.
/// </summary>
internal abstract class UpdateWriter
{
    public abstract void StartMap(int? count = null);

    public abstract void EndMap();

    public abstract void StartArray(int? count = null);

    public abstract void EndArray();

    public abstract void Name(string name);

    public abstract void Number(long value);

    public abstract void Number(double value);

    public abstract void String(string? value);

    public abstract void Bool(bool value);

    public abstract void Null();

    public void Property(string name, long value)
    {
        Name(name);
        Number(value);
    }

    public void Property(string name, double value)
    {
        Name(name);
        Number(value);
    }

    public void Property(string name, string? value)
    {
        Name(name);
        String(value);
    }

    public void Property(string name, bool value)
    {
        Name(name);
        Bool(value);
    }

    /// <summary>Values that changed, by position, with null for those that didn't, and none after the last that did.</summary>
    public void Changed(ReadOnlySpan<long> now, ReadOnlySpan<long> before, bool all)
    {
        var last = now.Length - 1;
        if (!all)
        {
            while (last >= 0 && now[last] == before[last])
                last--;
        }
        StartArray(last + 1);
        for (var i = 0; i <= last; i++)
        {
            if (all || now[i] != before[i])
                Number(now[i]);
            else
                Null();
        }
        EndArray();
    }

    /// <summary>
    /// Counts by key that changed, as a map: the whole array for a key new to the client, only what changed otherwise,
    /// and null for a key that's gone.
    /// </summary>
    public void Changes(List<CountChange> changes)
    {
        StartMap(changes.Count);
        foreach (var (key, now, before) in changes)
        {
            Name(key);
            if (now is null)
                Null();
            else
                Changed(now, before ?? now, all: before is null);
        }
        EndMap();
    }

    /// <summary>Named counts, as a map.</summary>
    public void Named(List<NamedCount> values)
    {
        StartMap(values.Count);
        foreach (var (name, value) in values)
            Property(name, value);
        EndMap();
    }

    /// <summary>A JSON value, as it is.</summary>
    public void Node(JsonNode? node)
    {
        switch (node)
        {
            case null:
                Null();
                break;
            case JsonObject map:
                StartMap(map.Count);
                foreach (var (name, value) in map)
                {
                    Name(name);
                    Node(value);
                }
                EndMap();
                break;
            case JsonArray array:
                StartArray(array.Count);
                foreach (var value in array)
                    Node(value);
                EndArray();
                break;
            default:
                Value(node);
                break;
        }
    }

    private void Value(JsonNode node)
    {
        switch (node.GetValueKind())
        {
            case JsonValueKind.String:
                String(node.GetValue<string>());
                break;
            case JsonValueKind.True or JsonValueKind.False:
                Bool(node.GetValue<bool>());
                break;
            case JsonValueKind.Number:
                if (node.AsValue().TryGetValue<long>(out var whole))
                    Number(whole);
                else
                    Number(node.GetValue<double>());
                break;
            default:
                Null();
                break;
        }
    }
}
