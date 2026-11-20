using System.Diagnostics;
using Meep2.Modules;
using Meep2.Runtime.Internal;
using Meep2.SDK.Modules;

namespace Meep2.Benchmarks;

/// <summary>
/// The pieces prompt 7 adds to each request, timed alone in a loop: reading an origin from baggage, finding and adding
/// to a place's counts, and writing the headers. Run with: dotnet run -c Release -- flows-micro
/// </summary>
internal static class FlowMicro
{
    public static int Run()
    {
        var baggage = Peers.Baggage(Peers.Origin(Guid.NewGuid().ToString(), "feeder", "m2"));
        var table = PeerTable.ForArrivals();
        for (var i = 0; i < 40; i++)
            table.For($"10.0.0.{i}").Count(received: 1);
        var place = "meep://" + Peers.OriginIn(baggage);
        var traceParent = $"00-{ActivityTraceId.CreateRandom().ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01";

        Time("Peers.OriginIn on a request's baggage", () => Peers.OriginIn(baggage));
        Time("A place's counts found among 41, and a request added", () =>
        {
            table.For(place).Count(received: 1, bytesReceived: 120);
            return 0;
        });
        var uri = new Uri("http://studio.local:8080/hooks/");
        Time("Peers.EndpointOf a Uri", () => Peers.EndpointOf(uri));
        Time("A request message with no headers of ours", () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://studio.local:8080/hooks/");
            return request.Headers.Count();
        });
        Time("The same, with traceparent and baggage", () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://studio.local:8080/hooks/");
            request.Headers.TryAddWithoutValidation("traceparent", traceParent);
            request.Headers.TryAddWithoutValidation("baggage", baggage);
            return request.Headers.Count();
        });
        Console.WriteLine($"Header bytes: traceparent {"traceparent: ".Length + traceParent.Length + 2}, baggage {"baggage: ".Length + baggage.Length + 2}, ask {"Meep-Host: ?".Length + 2}, answer {"Meep-Host: ".Length + 36 + 2}.");
        return 0;
    }

    private static void Time<T>(string name, Func<T> work)
    {
        for (var i = 0; i < 100_000; i++)
            work();
        var best = double.MaxValue;
        long allocated = 0;
        for (var round = 0; round < 5; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < 1_000_000; i++)
                work();
            best = Math.Min(best, clock.Elapsed.TotalNanoseconds / 1_000_000);
            allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / 1_000_000;
        }
        Console.WriteLine($"{name,-55} {best,7:0.0} ns  {allocated,4} B");
    }
}
