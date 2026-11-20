using System.Text.Json;
using System.Text.Json.Nodes;
using Meep2.Meeplang.Parsing;

namespace Meep2.Daemon.Subscriptions;

/// <summary>
/// What a subscription asks for, and how often: a JSON object, changed while it runs by JSON merge patches (RFC 7396),
/// in which null takes something out.
/// </summary>
/// <remarks>
/// <code>
/// {
///   "encoding": "cbor",              // or "json", the default; chosen once, when the subscription starts
///   "list": "2s",                    // the pipelines and their states
///   "host": "1s",                    // totals for the host, and where its Listens are
///   "runtime": "1s",                 // CPU, memory and garbage collection, from .NET's runtime metrics
///   "heartbeat": "5s",               // an update at least this often, even with nothing new; the default
///   "budget": 200000,                // bytes a second at most, on average; none unless given
///   "pipelines": {
///     "*":    { "totals": "2s" },    // every pipeline not named
///     "site": {
///       "totals": "1s",
///       "modules": { "*": "100ms", "m7": "1s" },   // each module's counts, by key; "*" for those not named
///       "links":   { "*": "100ms" },               // each link's counts, by key
///       "timing":  { "*": "1s" },                  // each module's handling-time histogram
///       "bytes":   { "*": "1s" },                  // bytes in and out, for modules that talk to the network
///       "arrivals":     { "*": "1s" },             // a Listen's counts by where messages come from
///       "destinations": { "*": "1s" }              // a Fetch's or Send's counts by where they go
///     }
///   }
/// }
/// </code>
/// <para>
/// Rates are durations as Meeplang writes them, from 50 ms. A named pipeline's entry is all of what's asked for it:
/// it doesn't add to <c>"*"</c>'s. Names that aren't running yet are kept, and served once they are.
/// </para>
/// </remarks>
internal sealed class SubscriptionPlan
{
    /// <summary>The shortest rate anything can be asked for at, as for the stats stream.</summary>
    public static readonly TimeSpan Fastest = TimeSpan.FromMilliseconds(50);

    /// <summary>The kinds of thing a pipeline's entry can ask for, each module or link at a rate of its own.</summary>
    public static readonly string[] PerModule = ["modules", "links", "timing", "bytes", "arrivals", "destinations"];

    private SubscriptionPlan(JsonObject json, bool cbor, TimeSpan? list, TimeSpan? host, TimeSpan? runtime, TimeSpan heartbeat, long? budget, IReadOnlyDictionary<string, PipelinePlan> pipelines)
    {
        Json = json;
        Cbor = cbor;
        List = list;
        Host = host;
        Runtime = runtime;
        Heartbeat = heartbeat;
        Budget = budget;
        Pipelines = pipelines;
    }

    /// <summary>The plan as the client last left it.</summary>
    public JsonObject Json { get; }

    public bool Cbor { get; }

    public TimeSpan? List { get; }

    public TimeSpan? Host { get; }

    public TimeSpan? Runtime { get; }

    public TimeSpan Heartbeat { get; }

    public long? Budget { get; }

    /// <summary>Each pipeline's entry, by name, with <c>"*"</c> for the rest.</summary>
    public IReadOnlyDictionary<string, PipelinePlan> Pipelines { get; }

    /// <summary>What's asked for a pipeline: its own entry, or <c>"*"</c>'s, or null for nothing.</summary>
    public PipelinePlan? For(string pipeline) =>
        Pipelines.TryGetValue(pipeline, out var plan) || Pipelines.TryGetValue("*", out plan) ? plan : null;

    /// <summary>Reads a plan.</summary>
    /// <exception cref="DaemonException">It isn't one, saying why.</exception>
    public static SubscriptionPlan Parse(JsonElement? json)
    {
        if (json is not { ValueKind: JsonValueKind.Object } value)
            throw new DaemonException(400, "A subscription needs a JSON object saying what it's for, such as {\"list\": \"2s\", \"pipelines\": {\"*\": {\"totals\": \"1s\"}}}.");
        return Of((JsonObject)JsonNode.Parse(value.GetRawText())!);
    }

    /// <summary>This plan changed by a merge patch, in which null takes something out.</summary>
    /// <exception cref="DaemonException">The patch isn't an object, or what it leaves isn't a plan. The plan is as it was.</exception>
    public SubscriptionPlan Change(JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object)
            throw new DaemonException(400, "A change is a JSON object, merged into the plan, with null to take something out.");
        var next = (JsonObject)Json.DeepClone();
        Merge(next, (JsonObject)JsonNode.Parse(patch.GetRawText())!);
        var changed = Of(next);
        if (changed.Cbor != Cbor)
            throw new DaemonException(400, "The encoding is chosen when the subscription starts. Start another to change it.");
        return changed;
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (name, value) in patch.ToList())
        {
            if (value is null)
                target.Remove(name);
            else if (value is JsonObject inner && target[name] is JsonObject existing)
                Merge(existing, inner);
            else
                target[name] = value.DeepClone();
        }
    }

    private static SubscriptionPlan Of(JsonObject json)
    {
        foreach (var (name, _) in json)
        {
            if (name is not ("encoding" or "list" or "host" or "runtime" or "heartbeat" or "budget" or "pipelines"))
                throw new DaemonException(400, $"A subscription doesn't take {name}. It takes encoding, list, host, runtime, heartbeat, budget and pipelines.");
        }
        var encoding = json["encoding"] is { } e ? Text(e, "encoding") : "json";
        if (encoding is not ("json" or "cbor"))
            throw new DaemonException(400, $"encoding is json or cbor, not {encoding}.");
        long? budget = null;
        if (json["budget"] is { } b)
        {
            if (b.GetValueKind() != JsonValueKind.Number || !b.AsValue().TryGetValue<long>(out var bytes) || bytes < 1000)
                throw new DaemonException(400, "budget is a whole number of bytes a second, at least 1000.");
            budget = bytes;
        }
        var pipelines = new Dictionary<string, PipelinePlan>(StringComparer.Ordinal);
        if (json["pipelines"] is { } p)
        {
            if (p is not JsonObject named)
                throw new DaemonException(400, "pipelines is an object, from each pipeline's name, or \"*\" for the rest, to what's asked for it.");
            foreach (var (name, entry) in named)
                pipelines[name] = PipelinePlan.Of(name, entry);
        }
        return new SubscriptionPlan(
            json,
            encoding == "cbor",
            Rate(json["list"], "list"),
            Rate(json["host"], "host"),
            Rate(json["runtime"], "runtime"),
            Rate(json["heartbeat"], "heartbeat") ?? TimeSpan.FromSeconds(5),
            budget,
            pipelines);
    }

    /// <summary>A rate: a duration as Meeplang writes it, from 50 ms.</summary>
    internal static TimeSpan? Rate(JsonNode? node, string what)
    {
        if (node is null)
            return null;
        var text = Text(node, what);
        return Durations.TryParse(text, out var every) && every >= Fastest
            ? every
            : throw new DaemonException(400, $"{what} is a duration of at least 50ms, such as 1s or 500ms, not {text}.");
    }

    private static string Text(JsonNode node, string what) =>
        node.GetValueKind() == JsonValueKind.String
            ? node.GetValue<string>()
            : throw new DaemonException(400, $"{what} is a string, such as \"1s\", not {node.ToJsonString()}.");
}

/// <summary>What a subscription asks for one pipeline.</summary>
/// <param name="Totals">How often to send its totals, or null for never.</param>
/// <param name="Rates">For modules, links, timing and bytes, the rate for each module or link by key, with <c>"*"</c> for the rest.</param>
internal sealed record PipelinePlan(TimeSpan? Totals, IReadOnlyDictionary<string, IReadOnlyDictionary<string, TimeSpan>> Rates)
{
    /// <summary>How often to send a kind of thing for one module or link, or null for never.</summary>
    public TimeSpan? RateOf(string kind, string key) =>
        Rates.TryGetValue(kind, out var rates) && (rates.TryGetValue(key, out var rate) || rates.TryGetValue("*", out rate)) ? rate : null;

    /// <summary>Whether it asks for anything of a kind at all.</summary>
    public bool Wants(string kind) => Rates.ContainsKey(kind);

    /// <summary>The rates it asks for a kind of thing at, without repeats.</summary>
    public IEnumerable<TimeSpan> RatesOf(string kind) =>
        Rates.TryGetValue(kind, out var rates) ? rates.Values.Distinct() : [];

    internal static PipelinePlan Of(string pipeline, JsonNode? node)
    {
        if (node is not JsonObject entry)
            throw new DaemonException(400, $"What's asked for {pipeline} is an object, such as {{\"totals\": \"1s\", \"modules\": {{\"*\": \"200ms\"}}}}.");
        var rates = new Dictionary<string, IReadOnlyDictionary<string, TimeSpan>>(StringComparer.Ordinal);
        foreach (var (name, value) in entry)
        {
            if (name == "totals")
                continue;
            if (!SubscriptionPlan.PerModule.Contains(name))
                throw new DaemonException(400, $"A pipeline's entry doesn't take {name}. It takes totals, modules, links, timing, bytes, arrivals and destinations.");
            if (value is not JsonObject keyed)
                throw new DaemonException(400, $"{pipeline}'s {name} is an object from each key, or \"*\" for the rest, to a rate, such as {{\"*\": \"200ms\"}}.");
            var byKey = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            foreach (var (key, rate) in keyed)
                byKey[key] = SubscriptionPlan.Rate(rate, $"{pipeline}'s {name} for {key}") ?? throw new DaemonException(400, $"{pipeline}'s {name} for {key} needs a rate.");
            rates[name] = byKey;
        }
        return new PipelinePlan(SubscriptionPlan.Rate(entry["totals"], $"{pipeline}'s totals"), rates);
    }
}
