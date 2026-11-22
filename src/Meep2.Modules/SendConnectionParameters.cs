using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Parameters for Send's inflections that send over a shared connection, TCP and WebSocket: where to, what, and how
/// hard to try.
/// </summary>
public abstract record SendConnectionParameters : ConnectionParameters
{
    /// <summary>
    /// The address to send to, worked out for each message: <c>ws://</c> or <c>wss://</c>, such as
    /// <c>wss://feed.example.com/prices</c>, or <c>tcp://</c> with a port, such as <c>tcp://printer.local:9100</c>.
    /// </summary>
    [Positional]
    public required Computed<string> Url { get; init; }

    /// <summary>What to send, worked out for each message. Without it, Send sends the message itself (on TCP, its text).</summary>
    public Computed<string>? Text { get; init; }

    /// <summary>How long to wait for a message to be sent, connecting included. 30 seconds unless set.</summary>
    public TimeSpan Timeout
    {
        get;
        init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(Timeout), "The timeout must be longer than zero.");
    } = TimeSpan.FromSeconds(30);

    /// <summary>How many times to try connecting before giving up (Q157). 3 unless set.</summary>
    public int Tries
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(Tries), "Send has to try at least once.");
    } = 3;

    /// <summary>How long to wait between tries. 1 second unless set.</summary>
    public TimeSpan RetryDelay
    {
        get;
        init => field = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(RetryDelay), "The delay can't be less than zero.");
    } = TimeSpan.FromSeconds(1);
}
