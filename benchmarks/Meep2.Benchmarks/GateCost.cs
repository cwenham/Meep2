using System.Diagnostics;
using System.Runtime;
using Meep2.Runtime;
using Meep2.Runtime.Verifying;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Security;
using Meep2.Security;

namespace Meep2.Benchmarks;

/// <summary>
/// What an @permission gate costs (permissions phase 4), measured with a stopwatch rather than BenchmarkDotNet, so it
/// runs quickly on a small machine too. Run with: dotnet run -c Release -- gates [--messages 200000]. Needs ssh-keygen.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Checking one message at a gate: its answer already kept on it; or a new message whose nearest checked
/// ancestor, the verification, is 1 to 1,000 generations up, so the gate walks that far; or the first check against
/// a verification, which works out what the credential holds.</item>
/// <item>A pipeline of a source and four steps that each make a new message, with no gate, a gate on the last module,
/// or one on every module, end to end, per message. Every message descends from one verification.</item>
/// </list>
/// </remarks>
internal static class GateCost
{
    private static readonly PermissionName s_readNotes = PermissionName.Parse("ReadNotes");

    public static async Task<int> RunAsync(string[] args)
    {
        var messages = args.SkipWhile(a => a != "--messages").Skip(1).Select(int.Parse).FirstOrDefault(200_000);
        Console.WriteLine($"{Environment.ProcessorCount} processors, .NET {Environment.Version}, {(GCSettings.IsServerGC ? "server" : "workstation")} GC, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}.");
        using var setup = await Setup.CreateAsync();
        Console.WriteLine();
        Checks(setup);
        Console.WriteLine();
        await PipelinesAsync(setup, messages);
        return 0;
    }

    // One gate's check of single messages, timed over many.
    private static void Checks(Setup setup)
    {
        var keeper = new Gatekeeper(() => setup.Options);
        var gate = new Gate(keeper, [s_readNotes]);
        Console.WriteLine("Checking one message at a gate:");

        var kept = new Count(0) { Parents = new Parents(setup.Verified) };
        Require(gate.Check(kept) is null);
        Report("its answer already kept on it", Time(1_000_000, _ => gate.Check(kept)));

        foreach (var depth in new[] { 1, 10, 100, 1000 })
        {
            // A chain of depth messages under the verification; each check is of a new child of its tip, so the walk
            // goes back depth generations to the verification, whose answer is kept on it after the first.
            Message tip = setup.Verified;
            for (var i = 1; i < depth; i++)
                tip = new Count(i) { Parents = new Parents(tip) };
            var iterations = depth >= 1000 ? 20_000 : 200_000;
            Report($"a new message, {depth,4} generation{(depth == 1 ? "" : "s")} below the verification", Time(iterations, _ => gate.Check(new Count(0) { Parents = new Parents(tip) })));
        }

        // The first check against a verification works out what its credential holds; later ones find it kept.
        Report("the first check against a new verification", Time(50_000, _ => gate.Check(new Count(0) { Parents = new Parents(setup.NewVerification()) })));
    }

    private static async Task PipelinesAsync(Setup setup, int messages)
    {
        Console.WriteLine($"A source and four steps that each make a new message, {messages:N0} messages, per message end to end:");
        foreach (var gates in new[] { 0, 1, 5 })
        {
            await ChainAsync(setup, messages / 10, gates); // warm up
            var best = TimeSpan.MaxValue;
            long allocated = 0;
            for (var run = 0; run < 5; run++)
            {
                GC.Collect();
                var totalBefore = GC.GetTotalAllocatedBytes(precise: true);
                var watch = Stopwatch.StartNew();
                await ChainAsync(setup, messages, gates);
                watch.Stop();
                var bytes = GC.GetTotalAllocatedBytes(precise: true) - totalBefore;
                if (watch.Elapsed < best)
                {
                    best = watch.Elapsed;
                    allocated = bytes;
                }
            }
            var label = gates switch { 0 => "no gate", 1 => "a gate on the last module", _ => "a gate on every module" };
            Console.WriteLine($"  {label,-40} {best.TotalNanoseconds / messages,8:N0} ns {allocated / (double)messages,8:N0} B");
        }
    }

    private static async Task ChainAsync(Setup setup, int messages, int gates)
    {
        var builder = new PipelineBuilder("gates", setup.Options with { BufferSize = messages });
        var gated = new NodeOptions { Permissions = [s_readNotes] };
        var previous = builder.AddSource(() => new Descendants(setup.Verified, messages));
        for (var i = 0; i < 4; i++)
        {
            var step = builder.Add(() => new ChainBenchmarks.Increment(), options: gates == 5 ? gated : null);
            builder.Connect(previous, step);
            previous = step;
        }
        var sink = new ChainBenchmarks.Sink();
        builder.Connect(previous, builder.Add(() => sink, options: gates > 0 ? gated : null));
        await using var pipeline = builder.Build();
        await pipeline.RunAsync();
        sink.Check(messages);
    }

    // Nanoseconds for each call, from the best of three timings.
    private static double Time(int iterations, Action<int> act)
    {
        for (var i = 0; i < Math.Min(iterations, 100_000); i++)
            act(i);
        var best = double.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < iterations; i++)
                act(i);
            best = Math.Min(best, Stopwatch.GetElapsedTime(started).Ticks * 100.0 / iterations);
        }
        return best;
    }

    private static void Report(string what, double nanoseconds) => Console.WriteLine($"  {what,-52} {nanoseconds,10:N0} ns");

    private static void Require(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("The gate refused a message it should have let through.");
    }

    // Emits messages that are each a child of the verification.
    private sealed class Descendants(VerifiedMessage verified, int count) : ASource
    {
        protected override Task RunAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
                context.Emit(new Count(i) { Parents = new Parents(verified) });
            return Task.CompletedTask;
        }
    }

    /// <summary>A trust store in a temporary folder with ReadNotes defined, and a credential issued by its CA holding it.</summary>
    private sealed class Setup : IDisposable
    {
        private readonly string _folder;

        private Setup(string folder, TrustStore store, Credential credential)
        {
            _folder = folder;
            Store = store;
            Credential = credential;
            Options = new PipelineOptions { Trust = TrustStoreSource.Of(store) };
            Verified = NewVerification();
        }

        public TrustStore Store { get; }

        public Credential Credential { get; }

        public PipelineOptions Options { get; }

        public VerifiedMessage Verified { get; }

        // A verification of the credential as Verify would make and emit it.
        public VerifiedMessage NewVerification() => new(Credential)
        {
            Kind = VerificationKind.Credential,
            Permissions = Credential.Permissions,
            TrustStoreVersion = Store.Version,
            RevocationVersion = Store.RevocationVersion,
            EmittedBy = new ModuleInfo(1, "Verify", null, "Credential"),
        };

        public static async Task<Setup> CreateAsync()
        {
            var folder = Directory.CreateTempSubdirectory("meep-gates-").FullName;
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var ca = Path.Combine(folder, "ca");
            var user = Path.Combine(folder, "alice");
            await SshKeygenAsync("-q", "-t", "ed25519", "-N", "", "-f", ca);
            await SshKeygenAsync("-q", "-t", "ed25519", "-N", "", "-f", user);
            await SshKeygenAsync("-q", "-s", ca, "-I", "alice", "-n", "alice", "-z", "1", "-V", "-1h:+8h", "-O", "clear", "-O", "extension:ReadNotes@meep", user + ".pub");
            var trust = TrustFolder.In(folder);
            trust.Create();
            trust.AddAuthority("ca", PublicKey.Parse("ca", File.ReadAllText(ca + ".pub").Trim()), [TrustStore.CredentialNamespace]);
            File.WriteAllText(trust.Permissions, "#permission ReadNotes\n");
            var credential = Credential.Parse(File.ReadAllText(user + "-cert.pub"));
            return new Setup(folder, TrustStore.Load(trust), credential);
        }

        private static async Task SshKeygenAsync(params string[] arguments)
        {
            var start = new ProcessStartInfo("ssh-keygen") { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"ssh-keygen {string.Join(' ', arguments)} failed: {await process.StandardError.ReadToEndAsync()}");
        }

        public void Dispose() => Directory.Delete(_folder, recursive: true);
    }
}
