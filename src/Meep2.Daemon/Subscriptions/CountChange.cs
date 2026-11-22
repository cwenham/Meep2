namespace Meep2.Daemon.Subscriptions;

/// <summary>
/// The counts for one key (a module, a link or a place) that changed: <see cref="Now"/> with <see cref="Before"/> as
/// last sent, <see cref="Before"/> null for a key new to the client, and both null for a key that's gone.
/// </summary>
internal readonly record struct CountChange(string Key, long[]? Now, long[]? Before);
