using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Meep2.Cli;
using Meep2.Daemon;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.Tests.Support;
using OpenTelemetry.Trace;
using Numbers = Meep2.Tests.Support.Numbers;

namespace Meep2.Tests;

/// <summary>Step 7's answers: restarting and keeping pipelines, the daemon's settings, WebSockets, sampling, services and --detach.</summary>
public class HostingTests
{
    private const string Ticker = """
        Interval 20ms
            WriteLine "{msg}"
        """;

    // Stopped by its error policy once 20 messages have failed, then restarted as its policy says.
    private const string Flaky = """
        defaults
            on-error = stop-pipeline at 50%, restart-pipeline after 50ms up to 1 time
        Interval 1ms
            Fetch '/no/such/file.json'
        """;

    [Test]
    public async Task Defaults_set_how_long_a_finished_pipeline_stays_listed()
    {
        var hour = await TestRun.Start("""
            defaults
                keep-finished = 1h
            Interval 10ms count=1
                Sink
            """, run: false);
        var none = await TestRun.Start("""
            defaults
                keep-finished = none
            Interval 10ms count=1
                Sink
            """, run: false);
        var usual = await TestRun.Start("""
            Interval 10ms count=1
                Sink
            """, run: false);
        var wrong = await TestRun.Start("""
            defaults
                keep-finished = 3
            Interval 10ms count=1
                Sink
            """, run: false);

        await Assert.That(hour.Result.Pipeline!.Options.KeepFinished).IsEqualTo(TimeSpan.FromHours(1));
        await Assert.That(none.Result.Pipeline!.Options.KeepFinished).IsEqualTo(TimeSpan.Zero);
        await Assert.That(usual.Result.Pipeline!.Options.KeepFinished).IsEqualTo(TimeSpan.FromHours(24));
        await Assert.That(wrong.Diagnostics).Contains("This needs a duration, as in keep-finished = 24h, or none.");
    }

    [Test]
    public async Task The_error_policy_says_how_a_pipeline_is_restarted()
    {
        var counted = await TestRun.Start("""
            defaults
                on-error = restart-pipeline after 1m up to 5 times in 1h
            Interval 10ms count=1
                Sink
            """, run: false);
        var total = await TestRun.Start("""
            defaults
                on-error = restart-pipeline after 30s up to 2 times
            Interval 10ms count=1
                Sink
            """, run: false);
        var never = await TestRun.Start("""
            defaults
                on-error = restart-pipeline never
            Interval 10ms count=1
                Sink
            """, run: false);
        var onAModule = await TestRun.Start("""
            Interval 10ms count=1
                Sink @on-error="restart-pipeline after 1s up to 2 times"
            """, run: false);
        var misspelt = await TestRun.Start("""
            defaults
                on-error = restart-pipline after 1s up to 2 times
            Interval 10ms count=1
                Sink
            """, run: false);
        var unfinished = await TestRun.Start("""
            defaults
                on-error = restart-pipeline after 1s up to 2 times in
            Interval 10ms count=1
                Sink
            """, run: false);

        var restart = counted.Result.Pipeline!.Options.ErrorPolicy.RestartPipeline!;
        await Assert.That(restart.Delay).IsEqualTo(TimeSpan.FromMinutes(1));
        await Assert.That(restart.Times).IsEqualTo(5);
        await Assert.That(restart.Within).IsEqualTo(TimeSpan.FromHours(1));
        await Assert.That(total.Result.Pipeline!.Options.ErrorPolicy.RestartPipeline!.Within).IsNull();
        await Assert.That(never.Result.Pipeline!.Options.ErrorPolicy.RestartPipeline).IsNull();
        await Assert.That(ErrorPolicy.Default.RestartPipeline).IsNull();
        await Assert.That(onAModule.Diagnostics).Contains("restart-pipeline is for the whole pipeline");
        await Assert.That(misspelt.Diagnostics).Contains("Did you mean 'restart-pipeline'?");
        await Assert.That(unfinished.Diagnostics).Contains("restart-pipeline can end with a stretch of time");
    }

    [Test]
    public async Task A_restart_policy_counts_restarts_within_its_stretch_of_time()
    {
        var now = DateTimeOffset.UtcNow;
        var hourly = new PipelineRestart(TimeSpan.FromSeconds(1), 2, TimeSpan.FromHours(1));
        var ever = new PipelineRestart(TimeSpan.FromSeconds(1), 2);

        await Assert.That(hourly.Allows([now.AddHours(-3), now.AddHours(-2), now.AddMinutes(-5)], now)).IsTrue();
        await Assert.That(hourly.Allows([now.AddMinutes(-10), now.AddMinutes(-5)], now)).IsFalse();
        await Assert.That(ever.Allows([now.AddHours(-3)], now)).IsTrue();
        await Assert.That(ever.Allows([now.AddHours(-3), now.AddHours(-2)], now)).IsFalse();
    }

    [Test]
    public async Task Meep_run_restarts_a_pipeline_as_its_policy_says_then_gives_up()
    {
        using var folder = new TemporaryFolder();
        var file = folder.Write("flaky.meep", Flaky.Replace("up to 1 time", "up to 2 times", StringComparison.Ordinal));
        var error = new StringWriter();

        var code = await Wait.For(MeepCommand.RunAsync(["run", file], new StringWriter(), TextWriter.Synchronized(error), folder.Path, CancellationToken.None));

        await Assert.That(code).IsEqualTo(3);
        await Assert.That(error.ToString()).Contains("Restarting it in 50ms (restart 1 of at most 2).");
        await Assert.That(error.ToString()).Contains("Restarting it in 50ms (restart 2 of at most 2).");
        await Assert.That(error.ToString()).DoesNotContain("restart 3");
    }

    [Test]
    public async Task A_pipeline_that_finishes_stays_listed_with_its_final_counts()
    {
        await using var lab = await DaemonLab.StartAsync();
        await lab.Client.StartAsync(lab.Write("counted.meep", """
            Interval 10ms count=3
                WriteLine "{msg}"
            """));

        var finished = await lab.WaitFor("counted", PipelineStates.Finished);
        var detail = await lab.Client.GetAsync("counted");
        var restarted = await lab.Client.StartAsync(finished.Path); // one that has ended is replaced
        await lab.WaitFor("counted", PipelineStates.Finished);
        await lab.Client.StopAsync("counted"); // takes it off the list

        await Assert.That(finished.Reason).IsEqualTo("its sources finished");
        await Assert.That(finished.Ended).IsNotNull();
        await Assert.That(detail.Modules[0].Counts.Emitted).IsEqualTo(3);
        await Assert.That(detail.Ended).IsEqualTo(finished.Ended);
        await Assert.That(restarted.State).IsEqualTo(PipelineStates.Running);
        await Assert.That(await lab.Client.ListAsync()).IsEmpty();
        await Assert.That(lab.Log.ToString()).Contains("[counted] has finished, because its sources finished");
    }

    [Test]
    public async Task A_pipeline_whose_file_says_so_leaves_the_list_when_it_finishes()
    {
        await using var lab = await DaemonLab.StartAsync();
        await lab.Client.StartAsync(lab.Write("brief.meep", """
            defaults
                keep-finished = none
            Interval 10ms count=2
                WriteLine "{msg}"
            """));

        await Wait.Until(() => lab.Log.ToString().Contains("[brief] has finished", StringComparison.Ordinal));

        await Assert.That(await lab.Client.ListAsync()).IsEmpty();
    }

    [Test]
    public async Task The_daemon_restarts_a_failed_pipeline_as_its_policy_says()
    {
        await using var lab = await DaemonLab.StartAsync();
        await lab.Client.StartAsync(lab.Write("flaky.meep", Flaky));

        var failed = await lab.WaitFor("flaky", PipelineStates.Failed);

        await Assert.That(failed.Restarts).IsEqualTo(1);
        await Assert.That(failed.Reason).Contains("ended in errors");
        await Assert.That(lab.Log.ToString()).Contains("Restarting it in 50ms (restart 1 of at most 1).");
        await Assert.That(lab.Log.ToString()).Contains("[flaky] is running again");
        await Assert.That(lab.Log.ToString()).Contains("[flaky] has failed, because");
    }

    [Test]
    public async Task Stopping_a_pipeline_that_waits_to_restart_stops_it_for_good()
    {
        await using var lab = await DaemonLab.StartAsync();
        await lab.Client.StartAsync(lab.Write("flaky.meep", Flaky.Replace("after 50ms", "after 1h", StringComparison.Ordinal)));
        var waiting = await lab.WaitFor("flaky", PipelineStates.Restarting);

        await Wait.For(lab.Client.StopAsync("flaky"));

        await Assert.That(waiting.Reason).Contains("It starts again at");
        await Assert.That(await lab.Client.ListAsync()).IsEmpty();
    }

    [Test]
    public async Task The_daemon_starts_the_pipelines_it_was_given()
    {
        await using var lab = await DaemonLab.StartAsync(("ticker.meep", Ticker), ("broken.meep", "Intervall 1s"));

        var running = await lab.Client.ListAsync();

        await Assert.That(running.Select(p => p.Name)).IsEquivalentTo(["ticker"]);
        await Assert.That(lab.Log.ToString()).Contains("[broken] broken didn't load.");
        await Assert.That(lab.Log.ToString()).Contains("There's no module called Intervall.");
    }

    [Test]
    public async Task The_settings_file_lists_pipelines_and_telemetry()
    {
        using var folder = new TemporaryFolder();
        var path = folder.Write("config.json", """
            {
              // Started when the daemon starts.
              "start": ["ticker.meep", "/elsewhere/feed.meep", "~/pipelines/home.meep"],
              "otlp": "http://localhost:4318",
              "sample": 0.25,
            }
            """);
        var typo = folder.Write("typo.json", """{ "strat": ["ticker.meep"] }""");
        var share = folder.Write("share.json", """{ "sample": 2 }""");

        var config = DaemonConfig.Load(path);

        await Assert.That(config.Start).IsEquivalentTo([
            Path.Combine(folder.Path, "ticker.meep"),
            "/elsewhere/feed.meep",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "pipelines", "home.meep")]);
        await Assert.That(config.Otlp).IsEqualTo("http://localhost:4318");
        await Assert.That(config.Sample).IsEqualTo(0.25);
        await Assert.That(DaemonConfig.Load(Path.Combine(folder.Path, "missing.json")).Start).IsEmpty();
        await Assert.That(() => DaemonConfig.Load(typo)).Throws<FormatException>().WithMessageContaining("there's no setting called strat");
        await Assert.That(() => DaemonConfig.Load(share)).Throws<FormatException>().WithMessageContaining("at most 1");
    }

    [Test]
    public async Task A_websocket_carries_the_api_both_ways()
    {
        await using var lab = await DaemonLab.StartAsync(("ticker.meep", Ticker));
        await using var socket = await lab.Client.OpenSocketAsync();
        var frames = new Frames(socket);

        await socket.SendAsync("list", "GET", "/pipelines");
        await socket.SendAsync("stats", "GET", "/pipelines/ticker/stats?every=50ms");
        await socket.SendAsync("tap", "GET", "/pipelines/ticker/modules/Interval/tap?where=" + Uri.EscapeDataString("msg.Count % 2 == 0"));
        await socket.SendAsync("inject", "POST", "/pipelines/ticker/modules/WriteLine/inject", text: "hello over a socket");
        await socket.SendAsync("42", "GET", "/nowhere");
        var list = await frames.Next(f => Id(f) == "list");
        var stats = await frames.Next(f => Id(f) == "stats");
        var tapped = await frames.Next(f => Id(f) == "tap");
        var injected = await frames.Next(f => Id(f) == "inject");
        var nowhere = await frames.Next(f => Id(f) == "42");
        await socket.CancelAsync("stats");
        var cancelled = await frames.Next(f => Id(f) == "stats" && f.GetProperty("event").GetString() == "end");
        await socket.SendRawAsync("not JSON");
        var refused = await frames.Next(f => f.GetProperty("id").ValueKind == JsonValueKind.Null);

        await Assert.That(list.GetProperty("status").GetInt32()).IsEqualTo(200);
        await Assert.That(list.GetProperty("body")[0].GetProperty("name").GetString()).IsEqualTo("ticker");
        await Assert.That(stats.GetProperty("event").GetString()).IsEqualTo("stats");
        await Assert.That(stats.GetProperty("data").GetProperty("modules").GetArrayLength()).IsEqualTo(2);
        await Assert.That(tapped.GetProperty("event").GetString()).IsEqualTo("message");
        await Assert.That(tapped.GetProperty("data").GetProperty("message").GetProperty("content").GetProperty("Count").GetInt32() % 2).IsEqualTo(0);
        await Assert.That(injected.GetProperty("status").GetInt32()).IsEqualTo(202);
        await Assert.That(nowhere.GetProperty("status").GetInt32()).IsEqualTo(404);
        await Assert.That(cancelled.GetProperty("data").GetProperty("reason").GetString()).IsEqualTo("cancelled");
        await Assert.That(refused.GetProperty("body").GetProperty("error").GetString()).StartsWith("That frame isn't JSON");
        await Wait.Until(() => lab.Output.ToString().Contains("hello over a socket", StringComparison.Ordinal));
    }

    [Test]
    public async Task A_websocket_has_the_capabilities_of_its_token()
    {
        await using var lab = await DaemonLab.StartAsync(("ticker.meep", Ticker));
        var token = DaemonTokens.Create(lab.Options.TokensFile, "dashboard", Capabilities.Observe);
        using var client = new DaemonClient(lab.Options.Socket, token);
        await using var socket = await client.OpenSocketAsync();
        var frames = new Frames(socket);

        await socket.SendAsync("1", "GET", "/pipelines");
        await socket.SendAsync("2", "POST", "/pipelines/ticker/modules/WriteLine/inject", text: "not allowed");
        var listed = await frames.Next(f => Id(f) == "1");
        var refused = await frames.Next(f => Id(f) == "2");
        using var stranger = new DaemonClient(lab.Options.Socket, "not-a-token");
        var unknown = await Assert.That(async () => { await stranger.OpenSocketAsync(); }).Throws<DaemonException>();

        await Assert.That(listed.GetProperty("status").GetInt32()).IsEqualTo(200);
        await Assert.That(refused.GetProperty("status").GetInt32()).IsEqualTo(403);
        await Assert.That(refused.GetProperty("body").GetProperty("error").GetString()).StartsWith("That needs the inject capability");
        await Assert.That(unknown!.Status).IsEqualTo(401);
    }

    [Test]
    public async Task Meep_start_without_a_daemon_says_how_to_get_one()
    {
        using var folder = new ShortTemporaryFolder();
        var file = folder.Write("ticker.meep", Ticker);
        var error = new StringWriter();

        var code = await Wait.For(MeepCommand.RunAsync(["start", file], new StringWriter(), error, folder.Path, CancellationToken.None));

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(error.ToString()).Contains("No meep daemon is running on");
        await Assert.That(error.ToString()).Contains("meep daemon install");
        await Assert.That(error.ToString()).Contains($"meep run {file}");
        await Assert.That(error.ToString()).Contains("--detach");
    }

    [Test]
    [Arguments("0.25", 0.25)]
    [Arguments("10%", 0.1)]
    [Arguments("1", 1.0)]
    public async Task Sample_takes_a_share_or_a_percentage(string written, double share)
    {
        var settings = HostSettings.Parse(["--sample", written, "ticker.meep"], new StringWriter());

        await Assert.That(settings!.Sample).IsEqualTo(share);
        await Assert.That(settings.Files).IsEquivalentTo(["ticker.meep"]);
    }

    [Test]
    public async Task Meep_run_refuses_a_share_it_cant_use()
    {
        var error = new StringWriter();

        var code = await MeepCommand.RunAsync(["run", "--sample", "2", "ticker.meep"], new StringWriter(), error, CancellationToken.None);

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(error.ToString()).Contains("--sample needs the share of traces to record");
    }

    [Test]
    public async Task The_default_sampler_keeps_about_one_trace_in_ten_and_whole_traces()
    {
        var sampler = Telemetry.Sampler(Telemetry.DefaultSample);
        var all = Telemetry.Sampler(1);
        var kept = 0;
        for (var i = 0; i < 10_000; i++)
        {
            var root = new SamplingParameters(default, ActivityTraceId.CreateRandom(), "Interval", ActivityKind.Producer);
            if (sampler.ShouldSample(root).Decision == SamplingDecision.RecordAndSample)
                kept++;
        }
        var sampledParent = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        var droppedParent = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.None);

        await Assert.That(kept).IsBetween(800, 1200);
        await Assert.That(sampler.ShouldSample(new SamplingParameters(sampledParent, sampledParent.TraceId, "Fetch", ActivityKind.Internal)).Decision).IsEqualTo(SamplingDecision.RecordAndSample);
        await Assert.That(sampler.ShouldSample(new SamplingParameters(droppedParent, droppedParent.TraceId, "Fetch", ActivityKind.Internal)).Decision).IsEqualTo(SamplingDecision.Drop);
        await Assert.That(all.ShouldSample(new SamplingParameters(default, ActivityTraceId.CreateRandom(), "Interval", ActivityKind.Producer)).Decision).IsEqualTo(SamplingDecision.RecordAndSample);
    }

    [Test]
    [NotInParallel("tracing")]
    public async Task A_trace_that_isnt_sampled_gets_no_spans_after_its_source()
    {
        var recorded = new ConcurrentQueue<Activity>();
        var unsampledChildren = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == MeepTelemetry.Name,
            // Roots by their trace id, as a ratio sampler does; the rest follow their parent.
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                if (options.Parent == default)
                    return (options.TraceId.ToHexString()[^1] - '0') % 2 == 0 ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.PropagationData;
                if ((options.Parent.TraceFlags & ActivityTraceFlags.Recorded) != 0)
                    return ActivitySamplingResult.AllDataAndRecorded;
                Interlocked.Increment(ref unsampledChildren);
                return ActivitySamplingResult.PropagationData;
            },
            ActivityStopped = activity =>
            {
                if (activity.Recorded && (string?)activity.GetTagItem("meep.pipeline") == "sampled")
                    recorded.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);
        var builder = new PipelineBuilder("sampled");
        var source = builder.AddSource(() => new Numbers([.. Enumerable.Range(1, 40)]), name: "Source");
        var doubler = builder.Add(() => new Map<Number>((number, context) => context.Emit(new Number(number.Value * 2))), name: "Doubler");
        builder.Chain(source, doubler, builder.Add(() => new Collect<Number>(), name: "Sink"));
        await using var pipeline = builder.Build();

        await Wait.For(pipeline.RunAsync());

        var roots = recorded.Where(s => s.Kind == ActivityKind.Producer).ToList();
        var hops = recorded.Where(s => s.Kind != ActivityKind.Producer).ToList();
        await Assert.That(roots.Count).IsBetween(1, 39); // some traces, not all
        await Assert.That(hops.Count).IsEqualTo(roots.Count * 2); // whole traces: the doubler and the sink
        foreach (var hop in hops)
            await Assert.That(roots.Select(r => r.TraceId)).Contains(hop.TraceId);
        await Assert.That(unsampledChildren).IsEqualTo(0); // nothing after an unsampled source asks for a span
    }

    [Test]
    public async Task Install_writes_a_launchd_agent_that_runs_the_daemon()
    {
        using var folder = new TemporaryFolder();
        var plan = DaemonService.Launchd(
            ["/usr/local/bin/meep", "daemon", "--otlp", "http://localhost:4318", "/Users/me/my <pipes>/ticker.meep"],
            "/Users/me/.meep",
            new Dictionary<string, string> { ["DOTNET_ROOT"] = "/Users/me/.dotnet" },
            Path.Combine(folder.Path, DaemonService.Label + ".plist"),
            501);

        await Assert.That(plan.Content).Contains("<string>/Users/me/my &lt;pipes&gt;/ticker.meep</string>");
        await Assert.That(plan.Content).Contains("<key>DOTNET_ROOT</key>\n        <string>/Users/me/.dotnet</string>");
        await Assert.That(plan.Content).Contains("<key>SuccessfulExit</key>");
        await Assert.That(plan.Content).Contains("<string>/Users/me/.meep/daemon/daemon.log</string>");
        await Assert.That(plan.Install.Select(c => c.ToString())).IsEquivalentTo([
            "launchctl bootout gui/501/io.github.cwenham.meep",
            $"launchctl bootstrap gui/501 {plan.File}"]);
        await Assert.That(plan.Install[0].MayFail).IsTrue();

        if (OperatingSystem.IsMacOS())
        {
            await File.WriteAllTextAsync(plan.File, plan.Content);
            var lint = Process.Start(new ProcessStartInfo("plutil", ["-lint", plan.File]) { RedirectStandardOutput = true })!;
            await lint.WaitForExitAsync();
            await Assert.That(lint.ExitCode).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Install_writes_a_systemd_user_service_that_runs_the_daemon()
    {
        var plan = DaemonService.Systemd(
            ["/usr/local/bin/meep", "daemon", "/home/me/my pipes/100%.meep"],
            new Dictionary<string, string> { ["MEEP_HOME"] = "/home/me/meep \"home\"" },
            "/home/me/.config/systemd/user/meep.service");

        await Assert.That(plan.Content).Contains("""ExecStart="/usr/local/bin/meep" "daemon" "/home/me/my pipes/100%%.meep" """.TrimEnd());
        await Assert.That(plan.Content).Contains("""Environment="MEEP_HOME=/home/me/meep \"home\"" """.TrimEnd());
        await Assert.That(plan.Content).Contains("Restart=on-failure");
        await Assert.That(plan.Content).Contains("WantedBy=default.target");
        await Assert.That(plan.Install.Select(c => c.ToString())).IsEquivalentTo([
            "systemctl --user daemon-reload",
            "systemctl --user enable meep.service",
            "systemctl --user restart meep.service"]);
    }

    [Test]
    public async Task Detach_runs_a_pipeline_in_the_background_until_it_is_killed()
    {
        Skip.When(OperatingSystem.IsWindows(), "--detach isn't supported on Windows yet.");
        using var folder = new TemporaryFolder();
        var file = folder.Write("ticker.meep", """
            Interval 20ms
                WriteLine "tick {msg.Count}"
            """);

        var (code, output, _) = await Meep(folder.Path, "run", "--detach", file);
        var pid = int.Parse(output.Split("process ")[1].Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
        var log = Path.Combine(folder.Path, "logs", "ticker.log");
        try
        {
            // It outlives the command that started it, and carries on writing to its log.
            await Wait.Until(() => File.Exists(log) && ReadShared(log).Contains("tick 3", StringComparison.Ordinal));
            await Assert.That(Running(pid)).IsTrue();
        }
        finally
        {
            Process.Start("kill", ["-TERM", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)])!.WaitForExit();
        }
        await Wait.Until(() => !Running(pid));

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(output).StartsWith("Running ticker in the background, as process ");
        await Assert.That(output).Contains($"Its output and errors go to {log}");
        await Assert.That(ReadShared(log)).Contains("Stopping, as asked.");
    }

    [Test]
    public async Task Detach_reports_a_file_that_doesnt_load_as_the_foreground_would()
    {
        Skip.When(OperatingSystem.IsWindows(), "--detach isn't supported on Windows yet.");
        using var folder = new TemporaryFolder();
        var file = folder.Write("broken.meep", "Intervall 1s");

        var (code, output, error) = await Meep(folder.Path, "run", "--detach", file);

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(output).IsEqualTo("");
        await Assert.That(error).Contains("There's no module called Intervall.");
    }

    // The meep program itself, as a separate process, since --detach starts meep again.
    private static async Task<(int Code, string Output, string Error)> Meep(string home, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "meep.exe" : "meep"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment["MEEP_HOME"] = home;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await Wait.For(process.WaitForExitAsync());
        return (process.ExitCode, await output, await error);
    }

    // Whether a process this one didn't start is still running.
    private static bool Running(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(stream).ReadToEnd();
    }

    private static string? Id(JsonElement frame) =>
        frame.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null ? id.ToString() : null;

    // Frames from a socket, read in the background, so a test can wait for the one it wants in any order.
    private sealed class Frames
    {
        private readonly ConcurrentQueue<JsonElement> _received = new();

        public Frames(DaemonSocket socket) => _ = Task.Run(async () =>
        {
            while (await socket.ReceiveAsync() is { } frame)
                _received.Enqueue(frame);
        });

        public async Task<JsonElement> Next(Func<JsonElement, bool> wanted)
        {
            JsonElement found = default;
            await Wait.Until(() =>
            {
                foreach (var frame in _received)
                {
                    if (wanted(frame))
                    {
                        found = frame;
                        return true;
                    }
                }
                return false;
            });
            return found;
        }
    }
}
