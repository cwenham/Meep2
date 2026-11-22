using Meep2.SDK.Messages;

namespace Meep2.Meeplang.Loading;

/// <summary>A message to ask inflections about parameters with when the pipeline loads, before any message exists.</summary>
internal sealed record StandIn : Message
{
    public static StandIn Instance { get; } = new();
}
