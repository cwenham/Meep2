namespace Meep2.Runtime.Internal;

/// <summary>
/// The modules one feeds, and for each, which of its inputs that is, counting from 1, and the count of messages that
/// link has carried.
/// </summary>
internal sealed record Fanout(InflectionNode[] Targets, int[] Inputs, LinkCount[] Counts)
{
    public static readonly Fanout None = new([], [], []);
}
