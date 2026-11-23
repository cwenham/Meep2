namespace Meep2.Daemon;

/// <summary>
/// Who may do what through the daemon's API: a token's holder what the token names (<see cref="DaemonTokens"/>), and
/// without one, the daemon's own user everything and nobody else anything.
/// </summary>
/// <remarks>
/// The tokens file is looked at no more than once a second (P3), and again at once, at most every 100 ms, for a token it
/// doesn't know, so a token made a moment ago works straight away. When a token is taken out of the file, what was
/// opened with it ends: <c>meep daemon revoke</c> tells the daemon at once (<see cref="Reload"/>), and a file edited by
/// hand is noticed within a second of the next request that shows a token (Q250).
/// </remarks>
/// <param name="tokensFile">The tokens file, read again whenever it changes.</param>
internal sealed class DaemonAccess(string tokensFile)
{
    // How long the tokens file is trusted not to have changed before it's looked at again.
    private static readonly TimeSpan s_lookEvery = TimeSpan.FromSeconds(1);

    // The least time between looks for a token it doesn't know, so a client showing bad tokens can't make it look on
    // every request.
    private static readonly TimeSpan s_lookAgainEvery = TimeSpan.FromMilliseconds(100);

    private readonly Lock _reading = new();
    private readonly Dictionary<string, CancellationTokenSource> _holders = new(StringComparer.Ordinal);
    private DaemonTokens _tokens = DaemonTokens.Load("");
    private DateTime _tokensWritten;
    // Far enough back that the first look is due, and not so far that subtracting it from now overflows.
    private long _lookedAt = long.MinValue / 2;
    private long _lookedAgainAt = long.MinValue / 2;

    /// <summary>What a client may do, by its <c>Authorization</c> header, or else by the user the system says <paramref name="peer"/> is.</summary>
    /// <exception cref="DaemonException">The token isn't known, or the client isn't the daemon's user.</exception>
    public AccessGrant GrantOf(string authorization, uint? peer)
    {
        if (authorization.StartsWith("Bearer ", StringComparison.Ordinal))
            return Find(authorization["Bearer ".Length..].Trim()) ?? throw new DaemonException(401, "That token isn't one this daemon knows.");
        var owner = PeerCredentials.CurrentUser;
        if (owner is null || peer == owner)
            return new AccessGrant(Capabilities.All, CancellationToken.None);
        throw new DaemonException(403, $"Only the user running the daemon may use it without a token, and you're user {peer?.ToString() ?? "unknown"}.");
    }

    /// <summary>Throws unless <paramref name="have"/> includes <paramref name="need"/>.</summary>
    /// <exception cref="DaemonException">It doesn't, saying which capability is needed.</exception>
    public static void Require(Capabilities have, Capabilities need)
    {
        if (need != Capabilities.None && !have.HasFlag(need))
            throw new DaemonException(403, $"That needs the {CapabilityNames.Of(need)[0]} capability, and you have {(have == Capabilities.None ? "none" : string.Join(", ", CapabilityNames.Of(have)))}.");
    }

    /// <summary>Reads the tokens file now, ending what was opened with tokens taken out of it. Returns their names.</summary>
    public IReadOnlyList<string> Reload()
    {
        var revoked = new List<(string Name, CancellationTokenSource Holders)>();
        lock (_reading)
            Look(revoked);
        End(revoked);
        return [.. revoked.Select(r => r.Name)];
    }

    // The grant a token gives, or null if it isn't one, looking at the file first when it's time to.
    private AccessGrant? Find(string token)
    {
        var revoked = new List<(string Name, CancellationTokenSource Holders)>();
        try
        {
            lock (_reading)
            {
                var now = Environment.TickCount64;
                if (now - _lookedAt >= (long)s_lookEvery.TotalMilliseconds)
                    Look(revoked);
                var found = _tokens.Find(token);
                if (found is null && now - _lookedAgainAt >= (long)s_lookAgainEvery.TotalMilliseconds)
                {
                    _lookedAgainAt = now;
                    Look(revoked);
                    found = _tokens.Find(token);
                }
                return found is var (name, capabilities) ? new AccessGrant(capabilities, HoldersOf(name).Token) : null;
            }
        }
        finally
        {
            End(revoked);
        }
    }

    // Reads the file again if it has changed since it was read, noting the holders of tokens it no longer has. Called
    // under the lock.
    private void Look(List<(string Name, CancellationTokenSource Holders)> revoked)
    {
        _lookedAt = Environment.TickCount64;
        var written = File.Exists(tokensFile) ? File.GetLastWriteTimeUtc(tokensFile) : default;
        if (written == _tokensWritten)
            return;
        var next = DaemonTokens.Load(tokensFile);
        foreach (var name in _tokens.GoneFrom(next))
        {
            if (_holders.Remove(name, out var holders))
                revoked.Add((name, holders));
        }
        _tokens = next;
        _tokensWritten = written;
    }

    // What's cancelled when the token called name is revoked. Called under the lock.
    private CancellationTokenSource HoldersOf(string name)
    {
        if (!_holders.TryGetValue(name, out var holders))
            _holders[name] = holders = new CancellationTokenSource();
        return holders;
    }

    // Ends what revoked tokens opened, outside the lock, since what it ends may run at once.
    private static void End(List<(string Name, CancellationTokenSource Holders)> revoked)
    {
        foreach (var (_, holders) in revoked)
        {
            holders.Cancel();
            holders.Dispose();
        }
    }
}
