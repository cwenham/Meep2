namespace Meep2.Daemon;

/// <summary>
/// Who may do what through the daemon's API: a token's holder what the token names (<see cref="DaemonTokens"/>), and
/// without one, the daemon's own user everything and nobody else anything.
/// </summary>
/// <param name="tokensFile">The tokens file, read again whenever it changes.</param>
internal sealed class DaemonAccess(string tokensFile)
{
    private DaemonTokens _tokens = DaemonTokens.Load("");
    private DateTime _tokensWritten;

    /// <summary>What a client may do, by its <c>Authorization</c> header, or else by the user the system says <paramref name="peer"/> is.</summary>
    /// <exception cref="DaemonException">The token isn't known, or the client isn't the daemon's user.</exception>
    public Capabilities CapabilitiesOf(string authorization, uint? peer)
    {
        if (authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Tokens().Find(authorization["Bearer ".Length..].Trim())?.Capabilities
                ?? throw new DaemonException(401, "That token isn't one this daemon knows.");
        }
        var owner = PeerCredentials.CurrentUser;
        if (owner is null || peer == owner)
            return Capabilities.All;
        throw new DaemonException(403, $"Only the user running the daemon may use it without a token, and you're user {peer?.ToString() ?? "unknown"}.");
    }

    /// <summary>Throws unless <paramref name="have"/> includes <paramref name="need"/>.</summary>
    /// <exception cref="DaemonException">It doesn't, saying which capability is needed.</exception>
    public static void Require(Capabilities have, Capabilities need)
    {
        if (need != Capabilities.None && !have.HasFlag(need))
            throw new DaemonException(403, $"That needs the {CapabilityNames.Of(need)[0]} capability, and you have {(have == Capabilities.None ? "none" : string.Join(", ", CapabilityNames.Of(have)))}.");
    }

    // The tokens file, read again when it changes.
    private DaemonTokens Tokens()
    {
        var written = File.Exists(tokensFile) ? File.GetLastWriteTimeUtc(tokensFile) : default;
        if (written != _tokensWritten)
        {
            _tokens = DaemonTokens.Load(tokensFile);
            _tokensWritten = written;
        }
        return _tokens;
    }
}
