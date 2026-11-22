namespace Meep2.SDK.Modules;

/// <summary>
/// Counts for one place at the other end of a module's connections: where messages arrive from, for a Listen
/// (<see cref="ModuleContext.Arrival"/>), or where they go, for a Fetch or Send (<see cref="ModuleContext.Destination"/>).
/// Tools use them to draw flows between pipelines and the endpoints outside Meep. Safe from any thread, at any time.
/// </summary>
/// <remarks>
/// A module keeps a bounded number of these. Past the bound, a place quiet for ten minutes is folded into <c>*</c>
/// (or <c>meep://*</c> for origins in Meep) to make room; with none quiet, the new place is counted there. A tally
/// kept after its place was folded counts there too, so totals stay whole.
/// </remarks>
public abstract class Tally
{
    internal Tally()
    {
    }

    /// <summary>A tally that counts nothing, for a module outside a pipeline.</summary>
    public static Tally None { get; } = new Nothing();

    /// <summary>Adds to the counts.</summary>
    /// <param name="received">Messages that came from the place: requests, WebSocket or TCP messages, responses.</param>
    /// <param name="sent">Messages that went to it: requests, replies, WebSocket or TCP messages.</param>
    /// <param name="bytesReceived">Bytes read from it, counted as <see cref="ModuleContext.CountBytes"/> counts them.</param>
    /// <param name="bytesSent">Bytes written to it.</param>
    /// <param name="failed">
    /// Exchanges with it that failed: a status of 500 or more, no answer, a timeout, a send that couldn't go.
    /// </param>
    /// <param name="rejected">
    /// Exchanges refused for the client's mistake: a status from 400 to 499, or a message too long to take.
    /// </param>
    public abstract void Count(long received = 0, long sent = 0, long bytesReceived = 0, long bytesSent = 0, long failed = 0, long rejected = 0);

    /// <summary>Counts an HTTP status: from 400 to 499 as rejected, from 500 as failed.</summary>
    public void Status(int status) => Count(failed: status >= 500 ? 1 : 0, rejected: status is >= 400 and < 500 ? 1 : 0);

    /// <summary>
    /// Says what's at the other end of a destination: the Meep host with this ID, or null for something that isn't
    /// Meep, as far as the module knows. A tool can then name both ends of a flow by ID.
    /// </summary>
    public virtual void Meep(string? hostId)
    {
    }

    private sealed class Nothing : Tally
    {
        public override void Count(long received = 0, long sent = 0, long bytesReceived = 0, long bytesSent = 0, long failed = 0, long rejected = 0)
        {
        }
    }
}
