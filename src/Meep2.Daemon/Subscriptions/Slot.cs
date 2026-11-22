namespace Meep2.Daemon.Subscriptions;

/// <summary>One thing a subscription asks for: what, for which pipeline entry (or none), at what rate.</summary>
internal readonly record struct Slot(UpdateSection What, string Entry, TimeSpan Every);
