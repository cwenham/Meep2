namespace Meep2.Daemon;

/// <summary>What a client may do, and a token that's cancelled if the daemon token it showed is revoked (Q250).</summary>
/// <param name="Capabilities">What it may do.</param>
/// <param name="Revoked">
/// Cancelled when its token is taken out of the tokens file, so a stream or socket it opened ends then; never cancelled
/// for the daemon's own user, who showed no token.
/// </param>
internal sealed record AccessGrant(Capabilities Capabilities, CancellationToken Revoked);
