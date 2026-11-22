using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Meep2.Cli;
using Meep2.Daemon;
using Meep2.Plugins;
using Meep2.Tests.Support;

namespace Meep2.Tests;

/// <summary>The daemon and its control API, and the meep commands that use it (step 7).</summary>
public class DaemonTests
{
    [Test]
    public async Task The_websocket_api_reads_a_query_as_the_http_api_does()
    {
        // As ASP.NET reads a query: + is a space, a name given twice keeps both values, and names match in any case.
        var query = ApiRequest.QueryOf("?force=true&name=a+b%21&tag=x&tag=y&Empty=");
        await Assert.That(query["force"]).IsEqualTo("true");
        await Assert.That(query["name"]).IsEqualTo("a b!");
        await Assert.That(query["tag"]).IsEqualTo("x,y");
        await Assert.That(query["Empty"]).IsEqualTo("");
        await Assert.That(ApiRequest.QueryOf("").Count).IsEqualTo(0);
    }

    private const string Ticker = """
        Interval 20ms
            WriteLine "{msg}"
        """;

    [Test]
    public async Task The_daemon_starts_lists_and_stops_pipelines()
    {
        await using var lab = await DaemonLab.StartAsync();
        var file = lab.Write("ticker.meep", Ticker);

        var started = await lab.Client.StartAsync(file);
        var listed = await lab.Client.ListAsync();
        await Wait.Until(() => lab.Output.ToString().Contains("Tick", StringComparison.Ordinal));
        var detail = await lab.Client.GetAsync("ticker");
        await lab.Client.StopAsync("ticker");
        var after = await lab.Client.ListAsync();

        await Assert.That(started.Name).IsEqualTo("ticker");
        await Assert.That(started.State).IsEqualTo("running");
        await Assert.That(listed.Select(p => p.Name)).IsEquivalentTo(["ticker"]);
        await Assert.That(detail.Modules.Select(m => m.Module)).IsEquivalentTo(["Interval #1", "WriteLine #2"]);
        await Assert.That(detail.Modules[0].Counts.Emitted).IsGreaterThan(0);
        await Assert.That(after).IsEmpty();
        await Assert.That(lab.Log.ToString()).Contains("[ticker] is running");
    }

    [Test]
    public async Task A_pipeline_says_whether_the_daemon_watches_its_file()
    {
        await using var lab = await DaemonLab.StartAsync();

        var watched = await lab.Client.StartAsync(lab.Write("watched.meep", Ticker), developerMode: false, watch: true);
        var plain = await lab.Client.StartAsync(lab.Write("plain.meep", Ticker));
        var listed = await lab.Client.ListAsync();
        var detail = await lab.Client.GetAsync("watched");
        var plainDetail = await lab.Client.GetAsync("plain");
        // One that may end before starting it returns, so it's only looked at once it has.
        await lab.Client.StartAsync(lab.Write("brief.meep", "Interval 10ms count=2\n    WriteLine \"{msg}\"\n"), developerMode: false, watch: true);
        var finished = await lab.WaitFor("brief", "finished");
        var finishedDetail = await lab.Client.GetAsync("brief");

        await Assert.That(watched.Watching).IsTrue();
        await Assert.That(plain.Watching).IsFalse();
        await Assert.That(listed.Single(p => p.Name == "watched").Watching).IsTrue();
        await Assert.That(listed.Single(p => p.Name == "plain").Watching).IsFalse();
        await Assert.That(detail.Watching).IsTrue();
        await Assert.That(plainDetail.Watching).IsFalse();

        // Once it has ended, nothing is watched, even if it ended before it was.
        await Assert.That(finished.Watching).IsFalse();
        await Assert.That(finishedDetail.Watching).IsFalse();
    }

    [Test]
    public async Task A_pipeline_that_doesnt_load_comes_back_with_its_errors()
    {
        await using var lab = await DaemonLab.StartAsync();
        var file = lab.Write("broken.meep", "Intervall 1s\n");

        var refused = await Assert.That(async () => { await lab.Client.StartAsync(file); }).Throws<DaemonException>();

        await Assert.That(refused!.Status).IsEqualTo(400);
        await Assert.That(refused.Message).IsEqualTo("broken didn't load.");
        await Assert.That(string.Join("\n", refused.Details)).Contains("(1,1): error: There's no module called Intervall.");
        await Assert.That(await lab.Client.ListAsync()).IsEmpty();
    }

    [Test]
    public async Task Two_pipelines_cant_share_a_name()
    {
        await using var lab = await DaemonLab.StartAsync();
        var file = lab.Write("ticker.meep", Ticker);
        await lab.Client.StartAsync(file);

        var refused = await Assert.That(async () => { await lab.Client.StartAsync(file); }).Throws<DaemonException>();

        await Assert.That(refused!.Status).IsEqualTo(409);
        await Assert.That(refused.Message).IsEqualTo("A pipeline called ticker is running already. Stop it first with: meep stop ticker");
    }

    [Test]
    public async Task A_tap_streams_the_messages_passing_a_module_and_injected_ones_show_up()
    {
        await using var lab = await DaemonLab.StartAsync();
        await lab.Client.StartAsync(lab.Write("ticker.meep", """
            Interval 20ms
                WriteLine "{msg}" as Printer
            """));
        using var stop = new CancellationTokenSource(Wait.Limit);
        var evens = new List<long>();
        string? injected = null;

        await foreach (var (message, _) in lab.Client.TapAsync("ticker", "Printer", where: "msg.Count % 2 == 0", cancellationToken: stop.Token))
        {
            evens.Add(message.GetProperty("content").GetProperty("Count").GetInt64());
            if (evens.Count == 3)
                break;
        }
        await foreach (var (message, _) in lab.Client.TapAsync("ticker", "Printer", cancellationToken: stop.Token))
        {
            if (message.GetProperty("type").GetString() == "Tick")
            {
                await lab.Client.InjectAsync("ticker", "Printer", "hello");
                continue;
            }
            injected = message.GetProperty("content").GetProperty("Text").GetString();
            break;
        }

        await Assert.That(evens.All(count => count % 2 == 0)).IsTrue();
        await Assert.That(injected).IsEqualTo("hello");
        await Wait.Until(() => lab.Output.ToString().Contains("hello", StringComparison.Ordinal));
    }

    [Test]
    public async Task Ended_pipelines_are_taken_off_the_list_on_a_timer_once_they_have_stayed_long_enough()
    {
        await using var lab = await DaemonLab.StartAsync(options => options with { PruneEvery = TimeSpan.FromMilliseconds(50) });
        await lab.Client.StartAsync(lab.Write("brief.meep", """
            defaults
                keep-finished = 1s
            Interval 10ms count=1
                WriteLine "{msg}"
            """));

        // Nothing asks for the list while it waits, so only the timer can take it off.
        await Wait.Until(() => lab.Log.ToString().Contains("[brief] has finished", StringComparison.Ordinal));
        await Assert.That(lab.Daemon.ListedNow).IsEqualTo(1);
        await Wait.Until(() => lab.Daemon.ListedNow == 0);
    }

    [Test]
    public async Task A_tap_over_a_websocket_drops_for_a_client_that_stops_reading()
    {
        await using var lab = await DaemonLab.StartAsync();
        var payload = new string('x', 2000);
        await lab.Client.StartAsync(lab.Write("flood.meep", $"""
            Interval 1ms payload='{payload}'
                WriteLine "." as Printer
            """));
        await lab.WaitFor("flood", "running");
        await using var socket = await lab.Client.OpenSocketAsync();
        await socket.SendAsync("t", "GET", "/pipelines/flood/modules/Printer/tap");
        await Wait.For(socket.ReceiveAsync());

        // Stop reading while about a thousand messages pass. Before S4, every one was queued for the socket, however
        // long that took, and the tap never dropped any.
        await Task.Delay(TimeSpan.FromSeconds(1));
        long dropped = 0;
        for (var i = 0; i < 400 && dropped == 0; i++)
        {
            var frame = await Wait.For(socket.ReceiveAsync()) ?? throw new InvalidOperationException("The socket closed.");
            if (frame.GetProperty("event").GetString() == "message")
                dropped = frame.GetProperty("data").GetProperty("dropped").GetInt64();
        }

        await Assert.That(dropped).IsGreaterThan(0);
    }

    [Test]
    public async Task A_tap_that_cant_be_opened_says_why()
    {
        await using var lab = await DaemonLab.StartAsync();
        await lab.Client.StartAsync(lab.Write("ticker.meep", Ticker));

        var noModule = await Assert.That(async () => await First(lab.Client.TapAsync("ticker", "Fetch"))).Throws<DaemonException>();
        var badCondition = await Assert.That(async () => await First(lab.Client.TapAsync("ticker", "1", where: "msg is Tik"))).Throws<DaemonException>();
        var noPipeline = await Assert.That(async () => await First(lab.Client.TapAsync("nothing", "1"))).Throws<DaemonException>();

        await Assert.That(noModule!.Message).IsEqualTo("ticker has no module called Fetch. Its modules are Interval #1, WriteLine #2.");
        await Assert.That(badCondition!.Message).Contains("There's no message type called Tik.");
        await Assert.That(noPipeline!.Message).IsEqualTo("There's no pipeline called nothing. The daemon has: ticker.");
    }

    [Test]
    public async Task The_stats_stream_sends_counts_as_they_change()
    {
        await using var lab = await DaemonLab.StartAsync();
        await lab.Client.StartAsync(lab.Write("ticker.meep", Ticker));
        using var stop = new CancellationTokenSource(Wait.Limit);
        var emitted = new List<long>();

        await foreach (var detail in lab.Client.WatchAsync("ticker", TimeSpan.FromMilliseconds(100), stop.Token))
        {
            emitted.Add(detail.Modules[0].Counts.Emitted);
            if (emitted.Count == 3)
                break;
        }

        await Assert.That(emitted[2]).IsGreaterThan(emitted[0]);
    }

    [Test]
    public async Task A_request_body_over_a_mebibyte_is_refused()
    {
        await using var lab = await DaemonLab.StartAsync();
        await lab.Client.StartAsync(lab.Write("ticker.meep", Ticker));

        var refused = await Assert.That(async () => { await lab.Client.InjectAsync("ticker", "WriteLine", new string('x', MeepDaemon.MaxBody + 1)); })
            .Throws<DaemonException>();
        var small = await lab.Client.InjectAsync("ticker", "WriteLine", new string('x', 1000));

        await Assert.That(refused!.Status).IsEqualTo(413);
        await Assert.That(refused.Message).IsEqualTo($"A request's body can be at most {MeepDaemon.MaxBody} bytes.");
        await Assert.That(small).IsNotNull();
    }

    [Test]
    public async Task A_pipeline_sent_as_text_can_be_as_large_as_one_from_a_url()
    {
        await using var lab = await DaemonLab.StartAsync();
        // '<' is escaped in JSON as <, six bytes for one, so this body is far over the usual limit.
        var comment = "// " + new string('<', MeepDaemon.MaxPipelineText - Ticker.Length - 16) + "\n";
        var tooLong = "// " + new string('x', MeepDaemon.MaxPipelineText) + "\n" + Ticker;

        var started = await lab.Client.StartTextAsync("large", comment + Ticker);
        var refused = await Assert.That(async () => { await lab.Client.StartTextAsync("too-large", tooLong); }).Throws<DaemonException>();

        await Assert.That(started.Name).IsEqualTo("large");
        await Assert.That(refused!.Status).IsEqualTo(413);
        await Assert.That(refused.Message).IsEqualTo("A pipeline's text can be at most 4 MiB, as one from a URL can.");
    }

    [Test]
    public async Task A_token_gives_only_the_capabilities_it_names()
    {
        await using var lab = await DaemonLab.StartAsync();
        await lab.Client.StartAsync(lab.Write("ticker.meep", Ticker));
        var token = DaemonTokens.Create(lab.Options.TokensFile, "dashboard", Capabilities.Observe);
        using var dashboard = new DaemonClient(lab.Options.Socket, token);
        using var stranger = new DaemonClient(lab.Options.Socket, "meep_not-a-real-token");

        var info = await dashboard.InfoAsync();
        var listed = await dashboard.ListAsync();
        var inject = await Assert.That(async () => { await dashboard.InjectAsync("ticker", "1", "hi"); }).Throws<DaemonException>();
        var stop = await Assert.That(() => dashboard.StopAsync("ticker")).Throws<DaemonException>();
        var unknown = await Assert.That(async () => { await stranger.ListAsync(); }).Throws<DaemonException>();
        var owner = await lab.Client.InfoAsync();

        await Assert.That(info.Capabilities).IsEquivalentTo(["observe"]);
        await Assert.That(listed.Count).IsEqualTo(1);
        await Assert.That(inject!.Status).IsEqualTo(403);
        await Assert.That(inject.Message).IsEqualTo("That needs the inject capability, and you have observe.");
        await Assert.That(stop!.Status).IsEqualTo(403);
        await Assert.That(unknown!.Status).IsEqualTo(401);
        await Assert.That(owner.Capabilities).IsEquivalentTo(["observe", "inject", "control"]);
        await Assert.That(File.ReadAllText(lab.Options.TokensFile)).DoesNotContain(token);
    }

    [Test]
    public async Task The_daemon_knows_which_user_connected()
    {
        Skip.When(OperatingSystem.IsWindows(), "Windows doesn't report who's at the other end of a local socket.");
        await using var lab = await DaemonLab.StartAsync();
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(lab.Options.Socket));

        await Assert.That(PeerCredentials.UserOf(socket)).IsNotNull();
        await Assert.That(PeerCredentials.UserOf(socket)).IsEqualTo(PeerCredentials.CurrentUser);
    }

    [Test]
    public async Task Only_one_daemon_can_use_a_socket_and_a_dead_ones_socket_is_cleared()
    {
        await using var lab = await DaemonLab.StartAsync();
        await using var second = new MeepDaemon(lab.Options);
        var refused = await Assert.That(() => second.StartAsync()).Throws<DaemonException>();

        var stale = Path.Combine(lab.SocketFolder, "stale.sock");
        using (var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            dead.Bind(new UnixDomainSocketEndPoint(stale)); // a socket file nothing listens on
        await using var third = new MeepDaemon(lab.Options with { SocketPath = stale });
        await third.StartAsync();
        using var client = new DaemonClient(stale);
        var info = await client.InfoAsync();

        await Assert.That(refused!.Message).StartsWith("A meep daemon is running already, on ");
        await Assert.That(info.Socket).IsEqualTo(stale);
    }

    [Test]
    public async Task Stopping_the_daemon_stops_its_pipelines_and_removes_its_socket()
    {
        await using var lab = await DaemonLab.StartAsync();
        await lab.Client.StartAsync(lab.Write("ticker.meep", Ticker));

        await lab.Client.StopDaemonAsync();
        await Wait.For(lab.Daemon.StopRequested);

        await Assert.That(lab.Daemon.Pipelines).IsEmpty();
        await Assert.That(File.Exists(lab.Options.Socket)).IsFalse();
        await Assert.That(lab.Log.ToString()).Contains("[ticker] has stopped, because it was asked to stop");
        var gone = await Assert.That(async () => { await lab.Client.ListAsync(); }).Throws<DaemonException>();
        await Assert.That(gone!.Message).StartsWith("No meep daemon is running on ");
        await Assert.That(gone.Message).Contains("meep daemon install");
    }

    [Test]
    public async Task The_meep_command_drives_the_daemon()
    {
        using var home = DaemonLab.ShortFolder();
        var options = new PluginOptions { Home = home.Path };
        var file = home.Write("ticker.meep", Ticker);
        using var stopDaemon = new CancellationTokenSource();
        var daemonOutput = new SharedText();
        var daemonLog = new SharedText();
        var daemon = MeepCommand.RunAsync(["daemon"], TextWriter.Synchronized(daemonOutput), TextWriter.Synchronized(daemonLog), options, stopDaemon.Token);
        await DaemonLab.UntilAnswering(Path.Combine(home.Path, "daemon", "meep.sock"), daemon);

        var start = await Cli(options, "start", file);
        var list = await Cli(options, "list");
        var stats = await Cli(options, "stats", "ticker");
        var inject = await Cli(options, "inject", "ticker", "WriteLine", """{"greeting":"hi"}""", "--json");
        var nonsense = await Cli(options, "tap", "ticker", "1", "--sample", "2");
        var stop = await Cli(options, "stop", "ticker");
        var stopped = await Cli(options, "daemon", "stop");
        var code = await Wait.For(daemon);

        await Assert.That(start.Output).StartsWith("Started ticker, from ");
        await Assert.That(list.Output).Contains("ticker  running");
        await Assert.That(stats.Output).Contains("WriteLine #2");
        await Assert.That(inject.Output).StartsWith("Sent JsonMessage ");
        await Assert.That(nonsense.Code).IsEqualTo(2);
        await Assert.That(stop.Output).IsEqualTo($"Stopped ticker.{Environment.NewLine}");
        await Assert.That(stopped.Code).IsEqualTo(0);
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(daemonOutput.ToString()).Contains("greeting");
    }

    [Test]
    [NotInParallel("tracing")]
    public async Task Meep_run_sends_spans_and_metrics_to_a_collector()
    {
        var received = new ConcurrentQueue<string>();
        await using var collector = new TestServer(async context =>
        {
            received.Enqueue(context.Request.Url!.AbsolutePath);
            await TestServer.Reply(context, 200, "", "application/x-protobuf");
        });
        using var folder = new TemporaryFolder();
        var file = folder.Write("counted.meep", """
            Interval 10ms count=3
                WriteLine "{msg.Count}"
            """);
        var output = new SharedText();

        var code = await Wait.For(MeepCommand.RunAsync(["run", "--otlp", collector.Url, "--sample", "1", file], output, new SharedText(), folder.Path, CancellationToken.None));

        await Assert.That(code).IsEqualTo(0);
        await Wait.Until(() => received.Contains("/v1/traces") && received.Contains("/v1/metrics"));
    }

    [Test]
    public async Task Meep_daemon_socket_shows_the_socket_in_full_and_the_daemons_id()
    {
        using var home = DaemonLab.ShortFolder();
        var options = new PluginOptions { Home = home.Path };
        var socket = Path.GetFullPath(Path.Combine(home.Path, "daemon", "meep.sock"));
        var before = await Cli(options, "daemon", "socket");

        using var stopDaemon = new CancellationTokenSource();
        var daemon = MeepCommand.RunAsync(["daemon"], new SharedText(), new SharedText(), options, stopDaemon.Token);
        await DaemonLab.UntilAnswering(socket, daemon);
        var running = await Cli(options, "daemon", "socket");
        await Cli(options, "daemon", "stop");
        await Wait.For(daemon);
        var help = await Cli(options, "--help");

        await Assert.That(before.Code).IsEqualTo(0);
        await Assert.That(before.Output).IsEqualTo($"{socket}{Environment.NewLine}{HostIdentity.IdOf(home.Path)}{Environment.NewLine}");
        await Assert.That(before.Error).Contains("No daemon is running there now");
        await Assert.That(running.Code).IsEqualTo(0);
        await Assert.That(running.Output).IsEqualTo(before.Output);
        await Assert.That(running.Error).IsEqualTo("");
        await Assert.That(help.Output).Contains("meep daemon socket");
    }

    private static async Task<(int Code, string Output, string Error)> Cli(PluginOptions options, params string[] args)
    {
        var output = new SharedText();
        var error = new SharedText();
        var code = await Wait.For(MeepCommand.RunAsync(args, output, error, options, CancellationToken.None));
        return (code, output.ToString(), error.ToString());
    }

    private static async Task<T> First<T>(IAsyncEnumerable<T> items)
    {
        await foreach (var item in items)
            return item;
        throw new InvalidOperationException("Nothing came.");
    }
}

/// <summary>A daemon of a test's own, on a socket in a short temporary folder, since socket paths are limited to about 100 characters.</summary>
public sealed class DaemonLab : IAsyncDisposable
{
    private readonly TemporaryFolder _home = new();
    private readonly ShortTemporaryFolder _socket = ShortFolder();

    private DaemonLab((string Name, string Text)[] start, Func<DaemonOptions, DaemonOptions>? change = null)
    {
        Options = new DaemonOptions
        {
            Plugins = new PluginOptions { Home = _home.Path },
            SocketPath = System.IO.Path.Combine(_socket.Path, "meep.sock"),
            Output = TextWriter.Synchronized(Output),
            Log = TextWriter.Synchronized(Log),
            Start = [.. start.Select(file => new DaemonStart(_home.Write(file.Name, file.Text)))],
        };
        if (change is not null)
            Options = change(Options);
        Daemon = new MeepDaemon(Options);
        Client = new DaemonClient(Options.Socket);
    }

    public DaemonOptions Options { get; }

    public MeepDaemon Daemon { get; }

    public DaemonClient Client { get; }

    public StringWriter Output { get; } = new SharedText();

    public StringWriter Log { get; } = new SharedText();

    public string SocketFolder => _socket.Path;

    /// <summary>Starts a daemon, which starts these pipeline files once it's listening.</summary>
    public static async Task<DaemonLab> StartAsync(params (string Name, string Text)[] start)
    {
        var lab = new DaemonLab(start);
        await lab.Daemon.StartAsync();
        return lab;
    }

    /// <summary>Starts a daemon with options changed from the usual, which starts these pipeline files once it's listening.</summary>
    public static async Task<DaemonLab> StartAsync(Func<DaemonOptions, DaemonOptions> change, params (string Name, string Text)[] start)
    {
        var lab = new DaemonLab(start, change);
        await lab.Daemon.StartAsync();
        return lab;
    }

    /// <summary>A folder with a short path, for sockets.</summary>
    public static ShortTemporaryFolder ShortFolder() => new();

    /// <summary>
    /// Waits until a daemon answers on its socket, or has ended. The socket's file appears when Kestrel binds,
    /// a moment before the daemon answers, so waiting for the file alone lets a client in too soon.
    /// </summary>
    public static Task UntilAnswering(string socket, Task daemon) => Wait.Until(async () =>
    {
        if (daemon.IsCompleted)
            return true;
        if (!File.Exists(socket))
            return false;
        using var client = new DaemonClient(socket);
        try
        {
            await client.InfoAsync();
            return true;
        }
        catch (DaemonException)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    });

    public string Write(string name, string text) => _home.Write(name, text);

    public string Home => _home.Path;

    /// <summary>Waits for a pipeline to reach a state, and returns how it looks then.</summary>
    public async Task<PipelineSummary> WaitFor(string name, string state)
    {
        PipelineSummary? found = null;
        await Wait.Until(async () =>
        {
            found = (await Client.ListAsync()).FirstOrDefault(p => p.Name == name);
            return found?.State == state;
        });
        return found!;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Daemon.DisposeAsync();
        _home.Dispose();
        _socket.Dispose();
    }
}

/// <summary>A temporary folder with a short name, since a Unix socket's path can only be about 100 characters long.</summary>
public sealed class ShortTemporaryFolder : IDisposable
{
    public ShortTemporaryFolder() => Path = Directory.CreateTempSubdirectory("m").FullName;

    public string Path { get; }

    public string Write(string name, string text)
    {
        var path = System.IO.Path.Combine(Path, name);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Left for the system to clean up.
        }
    }
}
