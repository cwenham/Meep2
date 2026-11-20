using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Meep2.Daemon;
using Meep2.Meeplang.Hosting;
using Meep2.Plugins;

namespace Meep2.Benchmarks;

/// <summary>
/// What origins, traces and the counts by origin and endpoint cost a busy Fetch to Listen pair (prompt 7). Run with:
/// dotnet run -c Release -- flows. It uses only what <c>main</c> has too, so the same file measures both.
/// </summary>
/// <remarks>
/// One daemon runs a Listen answering every request, and four Fetches asking it in parallel, each as fast as it gets
/// answers, over loopback. Each phase reports the requests answered a second, and the process's CPU time and
/// allocation for each request: the Fetch, the Listen and Send, and HTTP both ways.
/// </remarks>
internal static class FlowCost
{
    public static async Task<int> RunAsync(string[] args)
    {
        var seconds = args.SkipWhile(a => a != "--seconds").Skip(1).Select(int.Parse).FirstOrDefault(10);
        var home = Directory.CreateTempSubdirectory("meep-flow-").FullName;
        var socketFolder = Directory.CreateTempSubdirectory("m").FullName;
        var options = new DaemonOptions
        {
            Plugins = new PluginOptions { Home = home },
            SocketPath = Path.Combine(socketFolder, "meep.sock"),
            Output = TextWriter.Null,
            Log = TextWriter.Null,
        };
        await using var daemon = new MeepDaemon(options);
        await daemon.StartAsync();
        var port = FreePort();
        await daemon.StartPipelineAsync(new PipelineAddress(PipelineOrigin.Text, "site"), false, false, $$"""
            Listen "http://127.0.0.1:{{port}}/b/"
                Send text="ok"
            """, "site");
        await Task.Delay(500);
        // Four Fetches, each asked once a millisecond, as fast as loopback answers; a tick a Fetch is still busy with
        // waits in its buffer of 4, and older ones are dropped.
        await daemon.StartPipelineAsync(new PipelineAddress(PipelineOrigin.Text, "feeder"), false, false, $$"""
            Interval 1ms
                Fetch "http://127.0.0.1:{{port}}/b/" @buffer=4
                Fetch "http://127.0.0.1:{{port}}/b/" @buffer=4
                Fetch "http://127.0.0.1:{{port}}/b/" @buffer=4
                Fetch "http://127.0.0.1:{{port}}/b/" @buffer=4
            """, "feeder");
        Console.WriteLine($"{Environment.ProcessorCount} processors, .NET {Environment.Version}; {seconds} s a phase.");
        await Task.Delay(TimeSpan.FromSeconds(5)); // tiered compilation settles

        await Phase(daemon, "No tracing", seconds);
        using (Trace(0.1))
            await Phase(daemon, "Tracing, one trace in ten recorded", seconds);
        await Phase(daemon, "No tracing, again", seconds);
        if (args.Contains("--subscribe"))
        {
            using var client = await Subscribe(options.Socket, """{"pipelines": {"*": {"bytes": {"*": "100ms"}}}}""");
            await Phase(daemon, "A subscription to bytes at 100ms", seconds);
        }
        if (args.Contains("--places"))
        {
            using var client = await Subscribe(options.Socket, """{"pipelines": {"*": {"bytes": {"*": "100ms"}, "arrivals": {"*": "100ms"}, "destinations": {"*": "100ms"}}}}""");
            await Phase(daemon, "A subscription to bytes, arrivals and destinations at 100ms", seconds);
        }
        return 0;
    }

    private static async Task Phase(MeepDaemon daemon, string name, int seconds)
    {
        var process = Process.GetCurrentProcess();
        var answered = Answered(daemon);
        process.Refresh();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var elapsed = clock.Elapsed;
        process.Refresh();
        var requests = Answered(daemon) - answered;
        var cpuPer = (process.TotalProcessorTime - cpu).TotalMicroseconds / requests;
        var allocatedPer = (GC.GetTotalAllocatedBytes(precise: true) - allocated) / (double)requests;
        Console.WriteLine($"{name,-60} {requests / elapsed.TotalSeconds,8:0} requests/s  {cpuPer,6:0.0} µs CPU  {allocatedPer,7:0} B allocated, a request");
    }

    // Requests the Listen's Send has answered.
    private static long Answered(MeepDaemon daemon)
    {
        var site = daemon.Pipelines.First(p => p.Name == "site").Pipeline!;
        return site.ReadModules()[1].Stats.Handled;
    }

    // Records a share of traces, as meep daemon --otlp does, without exporting them.
    private static ActivityListener Trace(double share)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Meep2",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.Parent != default
                    ? (options.Parent.TraceFlags & ActivityTraceFlags.Recorded) != 0 ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.PropagationData
                    : Random.Shared.NextDouble() < share ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.PropagationData,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static async Task<IDisposable> Subscribe(string socketPath, string plan)
    {
        var socket = new ClientWebSocket();
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await unix.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                return new NetworkStream(unix, ownsSocket: true);
            },
        };
        await socket.ConnectAsync(new Uri("ws://localhost/ws"), new HttpMessageInvoker(handler), CancellationToken.None);
        var request = Encoding.UTF8.GetBytes($$"""{"id": "s", "method": "POST", "path": "/subscribe", "json": {{plan}}}""");
        await socket.SendAsync(request, WebSocketMessageType.Text, true, CancellationToken.None);
        var reading = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            var buffer = new byte[1 << 16];
            try
            {
                while (!reading.IsCancellationRequested)
                    await socket.ReceiveAsync(buffer, reading.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
            {
            }
        });
        return new Closing(socket, reading);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class Closing(ClientWebSocket socket, CancellationTokenSource reading) : IDisposable
    {
        public void Dispose()
        {
            reading.Cancel();
            socket.Abort();
            socket.Dispose();
        }
    }
}
