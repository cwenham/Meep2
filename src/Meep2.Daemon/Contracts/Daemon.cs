namespace Meep2.Daemon;

/// <summary>What the daemon says about itself, and what the asking client may do.</summary>
public sealed record DaemonInfo(string Version, int ProcessId, string Socket, IReadOnlyList<string> Capabilities, IReadOnlyList<string> Pipelines)
{
    /// <summary>The daemon's lasting ID, made the first time it ran and kept in <c>daemon/id</c> in Meep's folder.</summary>
    public string? Id { get; init; }

    /// <summary>The machine's names: its own name, its host name, and that name in full as DNS knows it.</summary>
    public IReadOnlyList<string>? Names { get; init; }

    /// <summary>The machine's addresses on the networks it's connected to, without loopback and link-local ones.</summary>
    public IReadOnlyList<string>? Addresses { get; init; }

    /// <summary>Where its pipelines' Listens are bound now.</summary>
    public IReadOnlyList<ListenInfo>? Listening { get; init; }

    /// <summary>Where its pipelines' Fetches and Sends send, as their addresses say, for matching them with Listens before anything runs.</summary>
    public IReadOnlyList<SendInfo>? Sending { get; init; }

    /// <summary>The daemon's clock as it answered, in microseconds since 1970 (UTC), for a client to estimate the offset between its clock and the daemon's.</summary>
    public long Clock { get; init; }
}

/// <summary>Where a Listen is bound.</summary>
/// <param name="Pipeline">Its pipeline.</param>
/// <param name="Module">Its key in the pipeline's graph.</param>
/// <param name="Label">How it's shown: its name, or its verb and number.</param>
/// <param name="Address">Where it's bound, as in <c>http://0.0.0.0:8080/hooks/</c> or <c>tcp://127.0.0.1:9000</c>.</param>
public sealed record ListenInfo(string Pipeline, string Module, string Label, string Address);

/// <summary>Where a Fetch or Send sends, as its address says (prompt 7's declared destinations).</summary>
/// <param name="Pipeline">Its pipeline.</param>
/// <param name="Module">Its key in the pipeline's graph.</param>
/// <param name="Label">How it's shown: its name, or its verb and number.</param>
/// <param name="Address">
/// The address, as in <c>http://studio.local:8080/hooks/</c>; for one worked out for each message, its expression as
/// written, as in <c>"https://api.example.com/stops/{msg.StopId}"</c>.
/// </param>
public sealed record SendInfo(string Pipeline, string Module, string Label, string Address)
{
    /// <summary>Whether the address is worked out for each message.</summary>
    public bool Computed { get; init; }

    /// <summary>
    /// The host and port it reaches, as destinations are counted (<c>api.example.com:443</c>), when the address fixes
    /// them, even if the rest is worked out for each message; null when the host or port is worked out too.
    /// </summary>
    public string? Endpoint { get; init; }
}
