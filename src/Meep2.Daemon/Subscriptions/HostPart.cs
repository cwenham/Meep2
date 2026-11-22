namespace Meep2.Daemon.Subscriptions;

/// <summary>What changed for the host in an update, and how it's written.</summary>
internal sealed class HostPart
{
    public List<NamedCount>? Totals { get; set; }

    public List<NamedCount>? States { get; set; }

    public List<ListenInfo>? Listens { get; set; }

    public List<SendInfo>? Sends { get; set; }

    public bool IsEmpty => Totals is null && States is null && Listens is null && Sends is null;

    /// <summary>Writes the part as a map of its sections.</summary>
    public void Write(UpdateWriter writer)
    {
        writer.StartMap((Totals is null ? 0 : 1) + (States is null ? 0 : 1) + (Listens is null ? 0 : 1) + (Sends is null ? 0 : 1));
        if (Totals is { } totals)
        {
            writer.Name(SubscriptionPlan.NameOf(UpdateSection.Totals));
            writer.Named(totals);
        }
        if (States is { } states)
        {
            writer.Name("states");
            writer.Named(states);
        }
        WriteListens(writer);
        WriteSends(writer);
        writer.EndMap();
    }

    private void WriteListens(UpdateWriter writer)
    {
        if (Listens is not { } listens)
            return;
        writer.Name("listens");
        writer.StartArray(listens.Count);
        foreach (var listen in listens)
        {
            writer.StartMap(4);
            writer.Property("pipeline", listen.Pipeline);
            writer.Property("module", listen.Module);
            writer.Property("label", listen.Label);
            writer.Property("address", listen.Address);
            writer.EndMap();
        }
        writer.EndArray();
    }

    private void WriteSends(UpdateWriter writer)
    {
        if (Sends is not { } sends)
            return;
        writer.Name("sends");
        writer.StartArray(sends.Count);
        foreach (var send in sends)
        {
            writer.StartMap(4 + (send.Computed ? 1 : 0) + (send.Endpoint is null ? 0 : 1));
            writer.Property("pipeline", send.Pipeline);
            writer.Property("module", send.Module);
            writer.Property("label", send.Label);
            writer.Property("address", send.Address);
            if (send.Computed)
                writer.Property("computed", true);
            if (send.Endpoint is { } endpoint)
                writer.Property("endpoint", endpoint);
            writer.EndMap();
        }
        writer.EndArray();
    }
}
