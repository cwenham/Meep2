namespace Meep2.Daemon.Subscriptions;

/// <summary>Who a daemon is, as a subscription's <c>hello</c> and <c>GET /</c> say it.</summary>
/// <param name="Id">Its lasting ID (<see cref="HostIdentity.IdOf"/>).</param>
/// <param name="Names">The machine's names.</param>
/// <param name="Addresses">The machine's addresses.</param>
/// <param name="Version">Meep's version.</param>
/// <param name="ProcessId">The daemon's process.</param>
internal sealed record HostInfo(string Id, IReadOnlyList<string> Names, IReadOnlyList<string> Addresses, string Version, int ProcessId);
