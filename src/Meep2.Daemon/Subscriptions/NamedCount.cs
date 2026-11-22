namespace Meep2.Daemon.Subscriptions;

/// <summary>One named count in an update: a total, or how many pipelines are in a state.</summary>
internal readonly record struct NamedCount(string Name, long Value);
