using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime;
using System.Text;
using Meep2.Daemon;
using Meep2.Meeplang.Hosting;
using Meep2.Modules;
using Meep2.Plugins;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Benchmarks;

/// <summary>
/// The memory the fam chain keeps alive (M8, for Q7): every message keeps its parents, so whatever holds a message
/// holds its ancestry too. Run with: dotnet run -c Release -- fam. Nothing is trimmed; this only measures.
/// </summary>
/// <remarks>
/// Each case reports memory still in use after a full, compacting collection, with what a module kept still held, less
/// the same reading taken before. Three cases, as Q7 and the refactoring plan ask:
/// <list type="bullet">
/// <item>Tick → Fetch a 5 MB file → Split it into 10,000 records, with a module keeping all the records, one record,
/// or only the records' text, as a cache might.</item>
/// <item>A WebSocket connection to a Listen kept open while 100,000 messages go through it, read as it goes, to see
/// whether a long-lived connection grows the daemon.</item>
/// <item>A source that makes each message from the one before (<c>last with { … }</c>), as a running total does, so
/// the chain grows without end while the last message is kept.</item>
/// </list>
/// </remarks>
internal static class FamCost
{
    private const int Records = 10_000;
    private const int FileBytes = 5 * 1024 * 1024;

    public static async Task<int> RunAsync(string[] args)
    {
        Console.WriteLine($"{Environment.ProcessorCount} processors, .NET {Environment.Version}, {(GCSettings.IsServerGC ? "server" : "workstation")} GC.");
        Console.WriteLine();
        await FanOutAsync();
        Console.WriteLine();
        await WebSocketAsync(args.SkipWhile(a => a != "--messages").Skip(1).Select(int.Parse).FirstOrDefault(100_000));
        Console.WriteLine();
        await DerivedChainAsync();
        return 0;
    }

    // Tick → Fetch (5 MB) → Split into 10,000, with the records kept in three ways.
    private static async Task FanOutAsync()
    {
        var folder = Directory.CreateTempSubdirectory("meep-fam-").FullName;
        var file = Path.Combine(folder, "records.txt");
        var line = new string('x', FileBytes / Records - 1);
        await File.WriteAllTextAsync(file, string.Join('\n', Enumerable.Repeat(line, Records)));
        Console.WriteLine($"Tick → Fetch a {FileBytes / (1024 * 1024)} MB file → Split into {Records:N0} records ({line.Length + 1} bytes each):");
        try
        {
            await WarmUpAsync(() => RunFanOutAsync(file, Keep.All));
            foreach (var keep in new[] { Keep.Nothing, Keep.TextOnly, Keep.One, Keep.All })
                Console.WriteLine($"  {Describe(keep),-42} {Mb(await KeptAsync(() => RunFanOutAsync(file, keep))),10}");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task<Keeper> RunFanOutAsync(string file, Keep keep)
    {
        var keeper = new Keeper(keep);
        var builder = new PipelineBuilder("fam", new PipelineOptions { BufferSize = Records * 2 });
        var tick = builder.AddSource(() => new OneTick(), name: "Tick");
        var fetch = builder.Add(() => new FileFetch(new FetchParameters { Url = Computed<string>.Constant(file), MaxSize = FileBytes * 2 }), name: "Fetch");
        var split = builder.Add(() => new Split(), name: "Split");
        builder.Chain(tick, fetch, split, builder.Add(() => keeper, name: "Keep"));
        await using (var pipeline = builder.Build())
            await pipeline.RunAsync();
        if (keeper.Seen != Records)
            throw new InvalidOperationException($"Only {keeper.Seen} of {Records} records arrived.");
        return keeper;
    }

    // A WebSocket held open while messages go through a Listen in a daemon, read on the way.
    private static async Task WebSocketAsync(int messages)
    {
        var home = Directory.CreateTempSubdirectory("meep-fam-").FullName;
        var socketFolder = Directory.CreateTempSubdirectory("m").FullName;
        var port = FreePort();
        Console.WriteLine($"A WebSocket to a Listen, held open while {messages:N0} messages of 100 bytes go through, each written by WriteLine:");
        try
        {
            await using var daemon = new MeepDaemon(new DaemonOptions
            {
                Plugins = new PluginOptions { Home = home },
                SocketPath = Path.Combine(socketFolder, "meep.sock"),
                Output = TextWriter.Null,
                Log = TextWriter.Null,
            });
            await daemon.StartAsync();
            await daemon.StartPipelineAsync(new PipelineAddress(PipelineOrigin.Text, "socket"), false, false, $$"""
                Listen "ws://127.0.0.1:{{port}}/ws/"
                    WriteLine "{msg.Text}"
                """, "socket");
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/"), CancellationToken.None);
            var frame = Encoding.UTF8.GetBytes(new string('m', 100));
            var hosted = daemon.Pipelines.Single(p => p.Name == "socket");

            long Handled() => hosted.Detail().Modules.Single(m => m.Verb == "WriteLine").Counts.Handled;

            // Sends, then waits until the Listen has handled everything sent, so nothing in flight counts as kept.
            async Task SendAsync(int count, long total)
            {
                for (var i = 0; i < count; i++)
                    await client.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                for (var waited = 0; waited < 1200 && Handled() < total; waited++)
                    await Task.Delay(50);
            }

            var sent = 5_000L;
            await SendAsync(5_000, sent); // warm up
            await Task.Yield();
            var start = Settled();
            var points = new List<(long Sent, long Bytes)>();
            foreach (var step in new[] { messages / 4, messages / 4, messages / 2 })
            {
                sent += step;
                await SendAsync(step, sent);
                await Task.Yield();
                points.Add((sent - 5_000, Settled() - start));
            }
            foreach (var (count, bytes) in points)
                Console.WriteLine($"  after {count,9:N0} messages {Mb(bytes),10}");
            var perMessage = (double)points[^1].Bytes / points[^1].Sent;
            Console.WriteLine($"  kept for each message: {perMessage,8:N1} bytes; a day at 10 a second: {Mb((long)(perMessage * 864_000))}");
            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(socketFolder, recursive: true);
        }
    }

    // A source that makes each message from the last, with the last kept, as a running total would.
    private static async Task DerivedChainAsync()
    {
        Console.WriteLine("A source making each message from the one before (last with { … }), the last one kept:");
        await WarmUpAsync(() => RunChainAsync(1_000));
        foreach (var length in new[] { 10_000, 100_000, 1_000_000 })
        {
            var kept = await KeptAsync(() => RunChainAsync(length));
            Console.WriteLine($"  {length,9:N0} messages long {Mb(kept),10}  ({(double)kept / length,6:N1} bytes a message)");
        }
    }

    private static async Task<Keeper> RunChainAsync(int length)
    {
        var keeper = new Keeper(Keep.One);
        var builder = new PipelineBuilder("chain", new PipelineOptions { BufferSize = length });
        builder.Chain(builder.AddSource(() => new Deriving(length), name: "Total"), builder.Add(() => keeper, name: "Keep"));
        await using (var pipeline = builder.Build())
            await pipeline.RunAsync();
        return keeper;
    }

    // Runs a case and lets go of what it returns. In a method of its own, so nothing of it is left in the caller's frame:
    // code that isn't optimised yet keeps an awaited result alive until the method it's in returns.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task WarmUpAsync(Func<Task<Keeper>> run) => await run();

    // What a case keeps once it has run: memory in use with what it returns still held, less memory in use before. Each
    // reading is taken on a fresh stack: a pipeline's continuations run inline, so the frames of a run that has just
    // finished, and what's in them, would otherwise still be there.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<long> KeptAsync(Func<Task<Keeper>> run)
    {
        await Task.Yield();
        var before = Settled();
        var kept = await run();
        await Task.Yield();
        var after = Settled();
        GC.KeepAlive(kept);
        return after - before;
    }

    // Memory in use once everything that can be collected has been, compacting the large object heap too.
    private static long Settled()
    {
        for (var i = 0; i < 3; i++)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):N2} MB";

    private static string Describe(Keep keep) => keep switch
    {
        Keep.All => "keeping all 10,000 records",
        Keep.One => "keeping one record",
        Keep.TextOnly => "keeping only the records' text",
        _ => "keeping nothing",
    };

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private enum Keep
    {
        All,
        One,
        TextOnly,
        Nothing,
    }

    internal sealed record Record(string Text) : Message;

    private sealed class OneTick : ASource
    {
        protected override Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            context.Emit(new Count(0));
            return Task.CompletedTask;
        }
    }

    // Splits a file's text into a record for each line, each made from the file.
    private sealed class Split : AInflection<FileText>
    {
        protected override ValueTask HandleAsync(FileText message, ModuleContext context, CancellationToken cancellationToken)
        {
            foreach (var line in message.Text.Split('\n'))
                context.Emit(new Record(line));
            return ValueTask.CompletedTask;
        }
    }

    // Makes each message from the one before.
    private sealed class Deriving(int length) : ASource
    {
        protected override Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            var last = new Count(0);
            context.Emit(last);
            for (var i = 1; i < length; i++)
            {
                last = last with { Value = i };
                context.Emit(last);
            }
            return Task.CompletedTask;
        }
    }

    // Keeps what it's told to, as a cache in a module would.
    private sealed class Keeper(Keep keep) : AInflection<Message>
    {
        private readonly List<object> _kept = [];

        public int Seen { get; private set; }

        protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
        {
            Seen++;
            switch (keep)
            {
                case Keep.All:
                    _kept.Add(message);
                    break;
                case Keep.One:
                    if (_kept.Count == 0)
                        _kept.Add(message);
                    else
                        _kept[0] = message;
                    break;
                case Keep.TextOnly when message is Record record:
                    _kept.Add(record.Text);
                    break;
            }
            return ValueTask.CompletedTask;
        }
    }
}
