using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Meep2.Daemon;
using Meep2.Meeplang.Hosting;
using Meep2.Plugins;

namespace Meep2.Benchmarks;

/// <summary>
/// What a subscription costs the daemon, and the wire, against the stats streams it replaces: many pipelines of many
/// modules, every count changing, sent ten times a second. Run with: dotnet run -c Release -- subscriptions
/// </summary>
/// <remarks>
/// The daemon runs in this process, with a client on its socket that only counts what arrives, so the CPU time measured
/// is the daemon's, the pipelines' and the client's receiving. Each phase's is set against a phase with no client, to
/// leave what serving the client costs. Allocation is measured the same way.
/// </remarks>
internal static class SubscriptionCost
{
    public static async Task<int> RunAsync(string[] args)
    {
        var pipelines = Option(args, "--pipelines", 50);
        var modules = Option(args, "--modules", 20);
        var seconds = Option(args, "--seconds", 10);
        var tick = Option(args, "--tick-ms", 10);

        var home = Directory.CreateTempSubdirectory("meep-cost-").FullName;
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
        var text = Text(modules, tick);
        for (var i = 0; i < pipelines; i++)
            await daemon.StartPipelineAsync(new PipelineAddress(PipelineOrigin.Text, $"p{i:00}"), false, false, text, $"p{i:00}");
        Console.WriteLine($"{pipelines} pipelines of {modules} modules, each module handling {1000 / tick} messages a second; {seconds} s a phase.");
        Console.WriteLine($"{Environment.ProcessorCount} processors, .NET {Environment.Version}, {(Debugger.IsAttached ? "debugger" : "no debugger")}.");
        await Task.Delay(TimeSpan.FromSeconds(3));

        await Task.Delay(TimeSpan.FromSeconds(7)); // so tiered compilation has settled
        PerUpdate(daemon);

        var names = Enumerable.Range(0, pipelines).Select(i => $"p{i:00}").ToList();
        var phases = new (string Name, Func<ClientWebSocket, Task> Start)[]
        {
            ("No client", _ => Task.CompletedTask),
            ("Stats streams, every=100ms", async socket =>
            {
                foreach (var name in names)
                    await Send(socket, $$"""{"id": "{{name}}", "path": "/pipelines/{{name}}/stats?every=100ms"}""");
            }),
            ("Subscription, JSON: modules and links at 100ms", socket =>
                Send(socket, """{"id": "s", "method": "POST", "path": "/subscribe", "json": {"pipelines": {"*": {"modules": {"*": "100ms"}, "links": {"*": "100ms"}}}}}""")),
            ("Subscription, CBOR: modules and links at 100ms", socket =>
                Send(socket, """{"id": "s", "method": "POST", "path": "/subscribe", "json": {"encoding": "cbor", "pipelines": {"*": {"modules": {"*": "100ms"}, "links": {"*": "100ms"}}}}}""")),
            ("Subscription, CBOR: and totals at 100ms, timing at 1s, list, host and runtime at 1s", socket =>
                Send(socket, """{"id": "s", "method": "POST", "path": "/subscribe", "json": {"encoding": "cbor", "list": "1s", "host": "1s", "runtime": "1s", "pipelines": {"*": {"totals": "100ms", "modules": {"*": "100ms"}, "links": {"*": "100ms"}, "timing": {"*": "1s"}}}}}""")),
            ("Subscription, CBOR: each pipeline's totals at 1s, as at host zoom", socket =>
                Send(socket, """{"id": "s", "method": "POST", "path": "/subscribe", "json": {"encoding": "cbor", "list": "2s", "host": "1s", "pipelines": {"*": {"totals": "1s"}}}}""")),
        };

        // A phase with no client before each, and one after the last, so each is set against the load around it.
        var interleaved = new List<(string Name, Func<ClientWebSocket, Task> Start)>();
        foreach (var phase in phases.Skip(1))
            interleaved.AddRange([phases[0], phase]);
        interleaved.Add(phases[0]);
        var rows = new List<(string Name, double Cpu, double Allocated, double Bytes, double Frames)>();
        foreach (var (name, start) in interleaved)
        {
            using var socket = name == "No client" ? null : await ConnectAsync(options.Socket);
            long bytes = 0, frames = 0;
            var counting = socket is null ? Task.CompletedTask : Task.Run(async () =>
            {
                var buffer = new byte[1 << 20];
                try
                {
                    while (socket.State == WebSocketState.Open)
                    {
                        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                        Interlocked.Add(ref bytes, result.Count);
                        if (result.EndOfMessage)
                            Interlocked.Increment(ref frames);
                    }
                }
                catch (WebSocketException)
                {
                    // Closed.
                }
            });
            if (socket is not null)
                await start(socket);
            await Task.Delay(TimeSpan.FromSeconds(2)); // the first, whole, updates go out here

            var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes(precise: false);
            var bytes0 = Interlocked.Read(ref bytes);
            var frames0 = Interlocked.Read(ref frames);
            var clock = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            process.Refresh();
            var elapsed = clock.Elapsed.TotalSeconds;
            rows.Add((
                name,
                (process.TotalProcessorTime - cpu).TotalMilliseconds / elapsed,
                (GC.GetTotalAllocatedBytes(precise: false) - allocated) / elapsed,
                (Interlocked.Read(ref bytes) - bytes0) / elapsed,
                (Interlocked.Read(ref frames) - frames0) / elapsed));
            if (socket is not null)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done.", CancellationToken.None);
                await counting;
            }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        Console.WriteLine();
        Console.WriteLine("Live, against the phases with no client either side of each:");
        Console.WriteLine();
        Console.WriteLine("| Phase | CPU ms/s | Without a client | Over that | Allocated MB/s over that | On the wire KB/s | Frames/s |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        for (var i = 1; i < rows.Count; i += 2)
        {
            var row = rows[i];
            var cpu = (rows[i - 1].Cpu + rows[i + 1].Cpu) / 2;
            var allocated = (rows[i - 1].Allocated + rows[i + 1].Allocated) / 2;
            Console.WriteLine($"| {row.Name} | {row.Cpu:0} | {cpu:0} | {row.Cpu - cpu:0} | {(row.Allocated - allocated) / 1e6:0.0} | {row.Bytes / 1024:0.0} | {row.Frames:0.0} |");
        }
        await daemon.StopAsync();
        Directory.Delete(home, recursive: true);
        Directory.Delete(socketFolder, recursive: true);
        return 0;
    }

    // What one update costs to make and encode, apart from everything else the process does: the stats streams' tick
    // (each pipeline's detail as JSON, as each stream sends it), and the subscription's, with only what changed in 100 ms
    // and whole.
    private static void PerUpdate(MeepDaemon daemon)
    {
        const int Rounds = 30;
        var hosted = daemon.Pipelines.OrderBy(p => p.Name).ToList();
        Console.WriteLine();
        Console.WriteLine($"One tick, timed alone: the median of {Rounds}, 100 ms apart.");
        Console.WriteLine();
        Console.WriteLine("| What | Time to make (ms) | Allocated (KB) | Bytes |");
        Console.WriteLine("|---|---|---|---|");

        Row("Stats streams: each pipeline's detail, as JSON", () =>
        {
            var bytes = 0;
            foreach (var pipeline in hosted)
            {
                var detail = pipeline.Detail();
                var buffer = new System.Buffers.ArrayBufferWriter<byte>();
                using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", pipeline.Name);
                    writer.WriteString("event", "stats");
                    writer.WritePropertyName("data");
                    System.Text.Json.JsonSerializer.Serialize(writer, detail, DaemonJson.Default.PipelineDetail);
                    writer.WriteEndObject();
                }
                bytes += buffer.WrittenSpan.ToArray().Length;
            }
            return bytes;
        });
        foreach (var (name, plan) in new[]
        {
            ("JSON", """{"pipelines": {"*": {"modules": {"*": "100ms"}, "links": {"*": "100ms"}}}}"""),
            ("CBOR", """{"encoding": "cbor", "pipelines": {"*": {"modules": {"*": "100ms"}, "links": {"*": "100ms"}}}}"""),
            ("CBOR, with totals and timing", """{"encoding": "cbor", "pipelines": {"*": {"totals": "100ms", "modules": {"*": "100ms"}, "links": {"*": "100ms"}, "timing": {"*": "100ms"}}}}"""),
            ("CBOR, modules only", """{"encoding": "cbor", "pipelines": {"*": {"modules": {"*": "100ms"}}}}"""),
            ("CBOR, links only", """{"encoding": "cbor", "pipelines": {"*": {"links": {"*": "100ms"}}}}"""),
            ("CBOR, totals only", """{"encoding": "cbor", "pipelines": {"*": {"totals": "100ms"}}}"""),
            ("CBOR, timing only", """{"encoding": "cbor", "pipelines": {"*": {"timing": {"*": "100ms"}}}}"""),
            ("CBOR, nothing", """{"encoding": "cbor", "list": "1h"}"""),
        })
        {
            using var document = System.Text.Json.JsonDocument.Parse(plan);
            var subscription = new Meep2.Daemon.Subscriptions.Subscription(Meep2.Daemon.Subscriptions.SubscriptionPlan.Parse(document.RootElement), daemon);
            subscription.UpdateNow(whole: true);
            Row($"Subscription, {name}: what changed in 100 ms", () => subscription.UpdateNow(whole: false).Length);
            Row($"Subscription, {name}: whole", () => subscription.UpdateNow(whole: true).Length);
        }
    }

    private static void Row(string what, Func<int> tick)
    {
        var times = new List<double>();
        var allocations = new List<long>();
        var sizes = new List<int>();
        for (var i = 0; i < 30; i++)
        {
            Thread.Sleep(100);
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            sizes.Add(tick());
            times.Add(clock.Elapsed.TotalMilliseconds);
            allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
        }
        static T Median<T>(List<T> values) => values.Order().ElementAt(values.Count / 2);
        Console.WriteLine($"| {what} | {Median(times):0.00} | {Median(allocations) / 1024.0:0} | {Median(sizes)} |");
    }

    // An Interval and a chain of modules under it, each indented under the last, so every module handles every tick.
    private static string Text(int modules, int tickMs)
    {
        var text = new StringBuilder($"Interval {tickMs}ms\n");
        for (var i = 1; i < modules - 1; i++)
            text.Append(new string(' ', 4 * i)).Append("Where msg.Count >= 0\n");
        text.Append(new string(' ', 4 * (modules - 1))).Append("WriteLine \"{msg.Count}\"\n");
        return text.ToString();
    }

    private static async Task<ClientWebSocket> ConnectAsync(string path)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        var client = new ClientWebSocket();
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        await client.ConnectAsync(new Uri("ws://meep/ws"), invoker, CancellationToken.None);
        return client;
    }

    private static Task Send(ClientWebSocket socket, string frame) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static int Option(string[] args, string name, int fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var value) ? value : fallback;
    }
}
