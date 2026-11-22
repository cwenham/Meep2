using System.Text.Json.Nodes;
using Meep2.Runtime;

namespace Meep2.Daemon.Subscriptions;

/// <summary>
/// A subscription's first event: the host, its clock, the names of the counts in each kind of array, the scale of the
/// timing histograms, and the plan.
/// </summary>
internal static class Hello
{
    public static void Write(UpdateWriter writer, HostInfo identity, string plan)
    {
        writer.StartMap(5);
        writer.Name("host");
        WriteHost(writer, identity);
        writer.Property("clock", DaemonClock.Now);
        writer.Name("counts");
        writer.StartMap(6);
        Names(writer, SubscriptionPlan.NameOf(UpdateSection.Modules), Subscription.ModuleCounts);
        Names(writer, SubscriptionPlan.NameOf(UpdateSection.Links), Subscription.LinkCounts);
        Names(writer, SubscriptionPlan.NameOf(UpdateSection.Bytes), Subscription.ByteCounts);
        Names(writer, SubscriptionPlan.NameOf(UpdateSection.Arrivals), Subscription.PeerCounts);
        Names(writer, SubscriptionPlan.NameOf(UpdateSection.Destinations), Subscription.PeerCounts);
        Names(writer, SubscriptionPlan.NameOf(UpdateSection.Totals), Subscription.TotalNames);
        writer.EndMap();
        writer.Name(SubscriptionPlan.NameOf(UpdateSection.Timing));
        writer.StartMap(2);
        writer.Property("scale", (long)HandlingTimes.Empty.Scale);
        writer.Property("unit", "s");
        writer.EndMap();
        writer.Name("plan");
        writer.Node(JsonNode.Parse(plan));
        writer.EndMap();
    }

    private static void WriteHost(UpdateWriter writer, HostInfo identity)
    {
        writer.StartMap(5);
        writer.Property("id", identity.Id);
        writer.Name("names");
        writer.StartArray(identity.Names.Count);
        foreach (var name in identity.Names)
            writer.String(name);
        writer.EndArray();
        writer.Name("addresses");
        writer.StartArray(identity.Addresses.Count);
        foreach (var address in identity.Addresses)
            writer.String(address);
        writer.EndArray();
        writer.Property("version", identity.Version);
        writer.Property("pid", identity.ProcessId);
        writer.EndMap();
    }

    private static void Names(UpdateWriter writer, string name, string[] names)
    {
        writer.Name(name);
        writer.StartArray(names.Length);
        foreach (var each in names)
            writer.String(each);
        writer.EndArray();
    }
}
