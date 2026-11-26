using System.Diagnostics;
using System.Runtime;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Security;

namespace Meep2.Benchmarks;

/// <summary>
/// What a pipeline's own credential and Meep's own permissions cost (permissions phase 6), measured with a stopwatch so
/// it runs quickly on a small machine too. Run with: dotnet run -c Release -- meep-permissions [--messages 200000].
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A source and four steps that each make a new message, with no gates: what every message descending from the
/// pipeline's verification costs a pipeline that checks nothing.</item>
/// <item>A source and a Save appending each message to a file: Save checks <c>meep.files.write</c> for each, which
/// for an unscoped permission is a kept answer; with <c>#holds</c> scoped to the file's folder, it also works out the
/// path's real form for each.</item>
/// </list>
/// </remarks>
internal static class MeepPermissionCost
{
    // The benchmark is the host that builds the pipelines, so it vouches for them as their owner.
    private static readonly PipelineCredential Owner = PipelineCredential.Owner("a benchmark built it");

    public static async Task<int> RunAsync(string[] args)
    {
        var messages = args.SkipWhile(a => a != "--messages").Skip(1).Select(int.Parse).FirstOrDefault(200_000);
        Console.WriteLine($"{Environment.ProcessorCount} processors, .NET {Environment.Version}, {(GCSettings.IsServerGC ? "server" : "workstation")} GC, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}.");
        var folder = Directory.CreateTempSubdirectory("meep-permissions-").FullName;
        try
        {
            Console.WriteLine($"Per message, end to end, best of five:");
            await ReportAsync("source and four steps, no gates", messages, () => ChainAsync(messages));
            await ReportAsync("source and a step that only checks meep.files.write", messages, () => CheckAsync(folder, messages));
            var saves = Math.Max(1, messages / 10);
            await ReportAsync($"source and Save ({saves:N0} messages)", saves, () => SaveAsync(folder, saves, holds: null));
            await ReportAsync($"source and Save, #holds scoped ({saves:N0})", saves, () => SaveAsync(folder, saves, holds: [PermissionName.Parse($"meep.files.write:{folder}/**")]));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
        return 0;
    }

    private static async Task ReportAsync(string label, int messages, Func<Task> run)
    {
        await run(); // warm up
        var best = TimeSpan.MaxValue;
        long allocated = 0;
        for (var round = 0; round < 5; round++)
        {
            GC.Collect();
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var watch = Stopwatch.StartNew();
            await run();
            watch.Stop();
            var bytes = GC.GetTotalAllocatedBytes(precise: true) - before;
            if (watch.Elapsed < best)
            {
                best = watch.Elapsed;
                allocated = bytes;
            }
        }
        Console.WriteLine($"  {label,-48} {best.TotalNanoseconds / messages,8:N0} ns {allocated / (double)messages,8:N0} B");
    }

    private static async Task ChainAsync(int messages)
    {
        var builder = new PipelineBuilder("chain", new PipelineOptions { BufferSize = messages, Credential = Owner });
        var previous = builder.AddSource(() => new Counting(messages));
        for (var i = 0; i < 4; i++)
        {
            var step = builder.Add(() => new ChainBenchmarks.Increment());
            builder.Connect(previous, step);
            previous = step;
        }
        var sink = new ChainBenchmarks.Sink();
        builder.Connect(previous, builder.Add(() => sink));
        await using var pipeline = builder.Build();
        await pipeline.RunAsync();
        sink.Check(messages);
    }

    private static async Task SaveAsync(string folder, int messages, IReadOnlyList<PermissionName>? holds)
    {
        var file = Path.Combine(folder, "saved.txt");
        File.Delete(file);
        var errors = 0;
        var builder = new PipelineBuilder("save", new PipelineOptions { BufferSize = messages, Credential = Owner, Holds = holds, UnhandledError = _ => Interlocked.Increment(ref errors) });
        builder.Chain(
            builder.AddSource(() => new Counting(messages)),
            builder.Add(() => new Save(new SaveParameters { Path = Computed<string>.Constant(file) })));
        await using var pipeline = builder.Build();
        await pipeline.RunAsync();
        if (errors > 0)
            throw new InvalidOperationException($"{errors} messages weren't saved.");
    }

    private static async Task CheckAsync(string folder, int messages)
    {
        var builder = new PipelineBuilder("check", new PipelineOptions { BufferSize = messages, Credential = Owner });
        var file = Path.Combine(folder, "checked.txt");
        builder.Chain(builder.AddSource(() => new Counting(messages)), builder.Add(() => new Checks(file)));
        await using var pipeline = builder.Build();
        await pipeline.RunAsync();
    }

    // Checks that each message may write a file, as Save does, and does nothing else.
    private sealed class Checks(string file) : AInflection<Count>
    {
        protected override ValueTask HandleAsync(Count message, ModuleContext context, CancellationToken cancellationToken)
        {
            context.DemandFile(MeepPermissions.FilesWrite, message, file);
            return ValueTask.CompletedTask;
        }
    }

    // Emits new messages with no parents of their own.
    private sealed class Counting(int count) : ASource
    {
        protected override Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
                context.Emit(new Count(i));
            return Task.CompletedTask;
        }
    }
}
