namespace Meep2.Runtime;

/// <summary>
/// How long a module has taken to handle each message since its pipeline started, as a histogram in OpenTelemetry's
/// exponential form (<see cref="Pipeline.GetHandlingTimes"/>). It counts up, as the module's counts do.
/// </summary>
/// <remarks>
/// <para>
/// Bucket <c>i</c> holds times greater than <c>base^i</c> seconds and at most <c>base^(i+1)</c>, where
/// <c>base = 2^(2^-scale)</c>: at <see cref="Scale"/> 3, eight buckets to each doubling, each about 9% wide. Every
/// module's histogram has the same scale, so histograms are merged by adding their buckets index by index, and a
/// pipeline's or a host's is the sum of its modules'.
/// </para>
/// <para>
/// Times are as long as the module's handler took, failures included, from 1 nanosecond to 18 hours: longer ones go in
/// the last bucket, and times too short for the clock to measure go in <see cref="Zero"/>.
/// </para>
/// </remarks>
/// <param name="Count">Messages timed: the buckets' counts and <see cref="Zero"/> added up.</param>
/// <param name="Sum">The times added up, in seconds.</param>
/// <param name="Min">The shortest time, in seconds, or 0 if none has been timed.</param>
/// <param name="Max">The longest time, in seconds, or 0 if none has been timed.</param>
/// <param name="Zero">Times too short for the clock to measure.</param>
/// <param name="Scale">How finely the buckets divide each doubling: 2^scale buckets to each.</param>
/// <param name="Offset">The index of the first of <see cref="Buckets"/>.</param>
/// <param name="Buckets">The count in each bucket from <see cref="Offset"/>, without empty buckets at either end.</param>
public sealed record HandlingTimes(long Count, double Sum, double Min, double Max, long Zero, int Scale, int Offset, IReadOnlyList<long> Buckets)
{
    /// <summary>A histogram with nothing in it.</summary>
    public static HandlingTimes Empty { get; } = new(0, 0, 0, 0, 0, Internal.TimingRecorder.Scale, 0, []);
}

/// <summary>Bytes a module has read from the network and written to it since its pipeline started (<see cref="Pipeline.GetTraffic"/>).</summary>
/// <param name="Received">Bytes read.</param>
/// <param name="Sent">Bytes written.</param>
public readonly record struct ModuleTraffic(long Received, long Sent);

/// <summary>One module's counts, busy time and bytes, read together (<see cref="Pipeline.ReadModules"/>).</summary>
/// <param name="Module">The module.</param>
/// <param name="Stats">Its counts.</param>
/// <param name="Traffic">Its bytes.</param>
/// <param name="Busy">How long it has spent handling messages.</param>
/// <param name="IsSource">Whether it's a source.</param>
public readonly record struct ModuleReading(Meep2.SDK.Modules.ModuleInfo Module, ModuleStats Stats, ModuleTraffic Traffic, TimeSpan Busy, bool IsSource);

/// <summary>Where a module is listening (<see cref="Pipeline.GetListening"/>).</summary>
/// <param name="Module">The module.</param>
/// <param name="Address">Where, as the module says it: <c>http://0.0.0.0:8080/hooks/</c>, <c>tcp://127.0.0.1:9000</c>.</param>
public readonly record struct ListeningAt(Meep2.SDK.Modules.ModuleInfo Module, string Address);

/// <summary>
/// A module's counts for one place at the other end of its connections (<see cref="Pipeline.GetArrivals"/>,
/// <see cref="Pipeline.GetDestinations"/>).
/// </summary>
/// <param name="Place">
/// For arrivals, <c>meep://</c> and the origin another Meep host gave, or the client's address; for destinations, the
/// host and port connected to. <c>*</c> (or <c>meep://*</c>) for the places past the module's bound, together, and
/// those folded there for being quiet when a new place needed the room.
/// </param>
/// <param name="Received">Messages that came from it.</param>
/// <param name="Sent">Messages that went to it.</param>
/// <param name="BytesReceived">Bytes read from it.</param>
/// <param name="BytesSent">Bytes written to it.</param>
/// <param name="Failed">Exchanges with it that failed: a status of 500 or more, no answer, a timeout.</param>
/// <param name="Meep">For a destination, the ID of the Meep host there, if it is one.</param>
public readonly record struct PeerCount(string Place, long Received, long Sent, long BytesReceived, long BytesSent, long Failed, string? Meep)
{
    /// <summary>Exchanges refused for the client's mistake: a status from 400 to 499, or a message too long.</summary>
    public long Rejected { get; init; }
}

/// <summary>Where a module sends, as its parameters say (<see cref="Pipeline.GetSending"/>).</summary>
/// <param name="Module">The module.</param>
/// <param name="Address">The address, or, for one worked out for each message, its expression as written.</param>
/// <param name="Computed">Whether it's worked out for each message.</param>
public readonly record struct SendingTo(Meep2.SDK.Modules.ModuleInfo Module, string Address, bool Computed);
