using System.Diagnostics;
using Meep2.Runtime;
using Meep2.SDK.Modules;

namespace Meep2.Benchmarks;

/// <summary>
/// What a flush costs while its module is busy (P2): many modules, each handling a message that takes 20 ms (waiting,
/// not working), and asking to be flushed 1 ms into it, so the flush waits for the gap between two messages. The CPU
/// it takes is set against the same modules not asking to be flushed. Run with: dotnet run -c Release -- flush-wait
/// [--modules 50] [--seconds 10]
/// </summary>
internal static class FlushWait
{
    public static async Task<int> RunAsync(string[] args)
    {
        var modules = Option(args, "--modules", 50);
        var seconds = Option(args, "--seconds", 10);
        Console.WriteLine($"{modules} modules each busy 20 ms a message; {seconds} s a phase; {Environment.ProcessorCount} processors, .NET {Environment.Version}.");
        await PhaseAsync(modules, 3, flush: true); // warming up
        foreach (var flush in new[] { false, true, false, true })
        {
            var (cpu, flushes, handled) = await PhaseAsync(modules, seconds, flush);
            Console.WriteLine($"{(flush ? "Flushing" : "Not flushing"),-13}: {cpu / seconds:N1} ms CPU a second, {handled / seconds:N0} messages and {flushes / seconds:N0} flushes a second");
        }
        return 0;
    }

    private static async Task<(double CpuMs, long Flushes, long Handled)> PhaseAsync(int modules, int seconds, bool flush)
    {
        var counts = new Counts();
        var builder = new PipelineBuilder("FlushWait");
        for (var i = 0; i < modules; i++)
            builder.Connect(builder.AddSource(() => new Paced()), builder.Add(() => new Slow(flush, counts)));
        await using var pipeline = builder.Build();
        var running = pipeline.RunAsync();
        await Task.Delay(500);
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var flushes = Interlocked.Read(ref counts.Flushes);
        var handled = Interlocked.Read(ref counts.Handled);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var used = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
        var result = (used, Interlocked.Read(ref counts.Flushes) - flushes, Interlocked.Read(ref counts.Handled) - handled);
        pipeline.Stop();
        await running;
        return result;
    }

    private sealed class Counts
    {
        public long Flushes;
        public long Handled;
    }

    // A message every 20 ms, so its module is nearly always busy.
    private sealed class Paced : ASource
    {
        protected override async Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            var i = 0;
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                context.Emit(new Count(i++));
        }
    }

    private sealed class Slow(bool flush, Counts counts) : AInflection<Count>
    {
        protected override async ValueTask HandleAsync(Count message, ModuleContext context, CancellationToken cancellationToken)
        {
            if (flush)
                context.FlushAfter(TimeSpan.FromMilliseconds(1));
            await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Increment(ref counts.Handled);
        }

        protected internal override ValueTask FlushAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref counts.Flushes);
            return ValueTask.CompletedTask;
        }
    }

    private static int Option(string[] args, string name, int fallback)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? int.Parse(args[at + 1], System.Globalization.CultureInfo.InvariantCulture) : fallback;
    }
}
