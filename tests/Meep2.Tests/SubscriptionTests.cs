using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Meep2.Daemon;
using Meep2.Daemon.Subscriptions;
using Meep2.Meeplang.Editing;
using Meep2.Meeplang.Loading;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.Runtime.Internal;
using Meep2.Tests.Support;

namespace Meep2.Tests;

/// <summary>
/// The daemon's subscriptions over <c>/ws</c> (Q202): one per connection, each thing at its own rate, only what changed,
/// changed as it goes, as JSON or CBOR; with handling-time histograms, bytes, the host's runtime, and who the host is.
/// </summary>
public class SubscriptionTests
{
    private const string Tick = """
        Interval 20ms
            WriteLine "{msg.Count}"
        """;

    private const string Done = """
        Interval 1ms count=3
            WriteLine "{msg.Count}"
        """;

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    // The next event of a kind on a stream, skipping others.
    private static async Task<JsonElement> Next(DaemonSocket socket, string id, string name)
    {
        while (true)
        {
            var frame = await Wait.For(socket.ReceiveAsync()) ?? throw new InvalidOperationException("The socket closed.");
            if (frame.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String && i.GetString() == id
                && frame.TryGetProperty("event", out var e) && e.GetString() == name)
                return frame.GetProperty("data");
        }
    }

    // The next update that has something at a path, such as pipelines.tick.modules.
    private static async Task<JsonElement> NextWith(DaemonSocket socket, string id, params string[] path)
    {
        while (true)
        {
            var update = await Next(socket, id, "update");
            if (At(update, path) is not null)
                return update;
        }
    }

    private static JsonElement? At(JsonElement element, params string[] path)
    {
        foreach (var part in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out element))
                return null;
        }
        return element;
    }

    private static async Task<DaemonSocket> Subscribe(DaemonLab lab, string plan, string id = "s")
    {
        var socket = await lab.Client.OpenSocketAsync();
        await socket.SendAsync(id, "POST", "/subscribe", Json(plan));
        return socket;
    }

    [Test]
    public async Task The_first_update_has_everything_asked_for_whole_keyed_as_the_graph_is()
    {
        await using var lab = await DaemonLab.StartAsync(("tick.meep", Tick));
        await lab.WaitFor("tick", "running");
        await Wait.Until(async () => (await lab.Client.GetAsync("tick")).Modules[1].Counts.Handled > 0);
        await using var socket = await Subscribe(lab, """
            {"list": "1h", "host": "1h", "runtime": "1h",
             "pipelines": {"tick": {"totals": "1h", "modules": {"*": "1h"}, "links": {"*": "1h"}, "timing": {"*": "1h"}}}}
            """);

        var hello = await Next(socket, "s", "hello");
        var update = await Next(socket, "s", "update");
        var graph = await lab.Client.GraphAsync("tick");

        await Assert.That(hello.GetProperty("host").GetProperty("id").GetString()).IsEqualTo(lab.Daemon.Id);
        await Assert.That(hello.GetProperty("counts").GetProperty("modules").EnumerateArray().Select(n => n.GetString()!)).IsEquivalentTo(Subscription.ModuleCounts);
        await Assert.That(hello.GetProperty("timing").GetProperty("scale").GetInt32()).IsEqualTo(3);
        await Assert.That(hello.GetProperty("plan").GetProperty("list").GetString()).IsEqualTo("1h");

        await Assert.That(update.GetProperty("seq").GetInt64()).IsEqualTo(1);
        var clock = DateTimeOffset.UnixEpoch.AddTicks(update.GetProperty("clock").GetInt64() * 10);
        await Assert.That((DateTimeOffset.UtcNow - clock).Duration()).IsLessThan(TimeSpan.FromMinutes(1));
        await Assert.That(update.GetProperty("list").GetProperty("tick").GetProperty("state").GetString()).IsEqualTo("running");
        await Assert.That(update.GetProperty("host").GetProperty("states").GetProperty("running").GetInt32()).IsEqualTo(1);
        await Assert.That(update.GetProperty("host").GetProperty("totals").GetProperty("sourced").GetInt64()).IsGreaterThan(0);
        var runtime = update.GetProperty("runtime");
        await Assert.That(runtime.TryGetProperty("host.memory.available", out _)).IsTrue();
        await Assert.That(runtime.EnumerateObject().Any(m => m.Name.StartsWith("dotnet.gc.", StringComparison.Ordinal))).IsTrue();

        var tick = update.GetProperty("pipelines").GetProperty("tick");
        await Assert.That(tick.GetProperty("graph").GetProperty("version").GetInt32()).IsEqualTo(graph.Version);
        await Assert.That(tick.GetProperty("modules").EnumerateObject().Select(m => m.Name)).IsEquivalentTo(graph.Modules.Select(m => m.Key));
        await Assert.That(tick.GetProperty("links").EnumerateObject().Select(l => l.Name)).IsEquivalentTo(graph.Links.Select(l => l.Key));
        // Whole the first time: every count, none left out.
        await Assert.That(tick.GetProperty("modules").GetProperty("m2").GetArrayLength()).IsEqualTo(Subscription.ModuleCounts.Length);
        await Assert.That(tick.GetProperty("modules").GetProperty("m2")[1].GetInt64()).IsGreaterThan(0);
        await Assert.That(tick.GetProperty("totals").GetProperty("sourced").GetInt64())
            .IsEqualTo(tick.GetProperty("modules").GetProperty("m1")[2].GetInt64());
        // The WriteLine's histogram has a time for each message it handled; the Interval, a source, has none.
        var times = tick.GetProperty("timing").GetProperty("m2");
        // Read after the counts, while it runs, so it may have more: exactly equal on a finished pipeline (below).
        await Assert.That(times.GetProperty("count").GetInt64()).IsGreaterThanOrEqualTo(tick.GetProperty("modules").GetProperty("m2")[1].GetInt64());
        await Assert.That(times.GetProperty("buckets").EnumerateArray().Sum(b => b.GetInt64()) + times.GetProperty("zero").GetInt64()).IsEqualTo(times.GetProperty("count").GetInt64());
        await Assert.That(tick.GetProperty("timing").TryGetProperty("m1", out _)).IsFalse();
    }

    [Test]
    public async Task Later_updates_carry_only_the_counts_that_changed()
    {
        await using var lab = await DaemonLab.StartAsync(("tick.meep", Tick), ("done.meep", Done));
        await lab.WaitFor("tick", "running");
        await lab.WaitFor("done", "finished");
        await using var socket = await Subscribe(lab, """{"pipelines": {"*": {"modules": {"*": "100ms"}}}}""");

        var first = await NextWith(socket, "s", "pipelines", "tick", "modules");
        await Assert.That(At(first, "pipelines", "done", "modules", "m2")!.Value.GetArrayLength()).IsEqualTo(Subscription.ModuleCounts.Length);
        var before = At(first, "pipelines", "tick", "modules", "m1")!.Value;
        var seq = first.GetProperty("seq").GetInt64();

        var next = await NextWith(socket, "s", "pipelines", "tick", "modules", "m1");
        await Assert.That(next.GetProperty("seq").GetInt64()).IsEqualTo(seq + 1);
        // The finished pipeline's counts don't change, so it isn't in it at all.
        await Assert.That(At(next, "pipelines", "done")).IsNull();
        // The Interval received nothing and handled nothing, so those are null; what it emitted went up; and nothing
        // after the last count that changed is sent.
        var m1 = At(next, "pipelines", "tick", "modules", "m1")!.Value;
        await Assert.That(m1[0].ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(m1[1].ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(m1[2].GetInt64()).IsGreaterThan(before[2].GetInt64());
        await Assert.That(m1.GetArrayLength()).IsLessThan(Subscription.ModuleCounts.Length);
        for (var i = 0; i < m1.GetArrayLength(); i++)
        {
            if (m1[i].ValueKind != JsonValueKind.Null)
                await Assert.That(m1[i].GetInt64()).IsNotEqualTo(before[i].GetInt64());
        }
    }

    [Test]
    public async Task A_subscription_changes_in_place_and_refuses_a_change_that_isnt_a_plan()
    {
        await using var lab = await DaemonLab.StartAsync(("tick.meep", Tick));
        await lab.WaitFor("tick", "running");
        await using var socket = await Subscribe(lab, """{"list": "1h"}""");
        await NextWith(socket, "s", "list");

        // One module of one pipeline, added without reconnecting: it comes whole, and nothing else does.
        await socket.ChangeAsync("s", Json("""{"pipelines": {"tick": {"modules": {"m2": "100ms"}}}}"""));
        var plan = await Next(socket, "s", "plan");
        await Assert.That(plan.GetProperty("pipelines").GetProperty("tick").GetProperty("modules").GetProperty("m2").GetString()).IsEqualTo("100ms");
        var update = await NextWith(socket, "s", "pipelines", "tick", "modules");
        await Assert.That(At(update, "pipelines", "tick", "modules")!.Value.EnumerateObject().Select(m => m.Name)).IsEquivalentTo(["m2"]);
        await Assert.That(At(update, "pipelines", "tick", "modules", "m2")!.Value.GetArrayLength()).IsEqualTo(Subscription.ModuleCounts.Length);

        // A rate that's too fast is refused, and the plan stays as it was.
        await socket.ChangeAsync("s", Json("""{"pipelines": {"tick": {"modules": {"m2": "5ms"}}}}"""));
        var problem = await Next(socket, "s", "problem");
        await Assert.That(problem.GetProperty("error").GetString()).Contains("at least 50ms");
        await NextWith(socket, "s", "pipelines", "tick", "modules", "m2");

        // Dropped: nothing more is sent for it.
        await socket.ChangeAsync("s", Json("""{"pipelines": null, "heartbeat": "100ms"}"""));
        await Next(socket, "s", "plan");
        var quiet = Environment.TickCount64;
        while (Environment.TickCount64 - quiet < 600)
        {
            var later = await Next(socket, "s", "update");
            await Assert.That(At(later, "pipelines")).IsNull();
        }
    }

    [Test]
    public async Task A_change_sent_straight_after_subscribing_is_taken()
    {
        await using var lab = await DaemonLab.StartAsync(("done.meep", Done));
        await lab.WaitFor("done", "finished");
        await using var socket = await Subscribe(lab, """{"list": "1h"}""");
        await socket.ChangeAsync("s", Json("""{"pipelines": {"done": {"modules": {"m2": "1h"}}}}"""));

        var plan = await Next(socket, "s", "plan");
        await Assert.That(At(plan, "pipelines", "done", "modules", "m2")!.Value.GetString()).IsEqualTo("1h");
        var update = await NextWith(socket, "s", "pipelines", "done", "modules", "m2");
        await Assert.That(At(update, "pipelines", "done", "modules", "m2")!.Value[1].GetInt64()).IsEqualTo(3);
    }

    [Test]
    public async Task A_graph_change_is_sent_straight_away_whatever_the_rates()
    {
        await using var lab = await DaemonLab.StartAsync();
        var file = lab.Write("tick.meep", Tick);
        await lab.Client.StartAsync(file);
        await using var socket = await Subscribe(lab, """{"pipelines": {"tick": {"totals": "1h"}}}""");
        var first = await NextWith(socket, "s", "pipelines", "tick", "graph");
        await Assert.That(At(first, "pipelines", "tick", "graph", "reason")).IsNull();

        await File.WriteAllTextAsync(file, Tick + "\n    WriteLine \"again\"\n");
        await lab.Client.ReloadAsync("tick");
        var changed = await NextWith(socket, "s", "pipelines", "tick", "graph");
        var graph = await lab.Client.GraphAsync("tick");
        await Assert.That(At(changed, "pipelines", "tick", "graph", "version")!.Value.GetInt32()).IsEqualTo(graph.Version);
        await Assert.That(At(changed, "pipelines", "tick", "graph", "reason")!.Value.GetString()).IsEqualTo("reload");
    }

    [Test]
    public async Task Resending_sends_everything_whole_again()
    {
        await using var lab = await DaemonLab.StartAsync(("done.meep", Done));
        await lab.WaitFor("done", "finished");
        await using var socket = await Subscribe(lab, """{"pipelines": {"done": {"modules": {"*": "100ms"}, "links": {"*": "100ms"}}}}""");
        var first = await NextWith(socket, "s", "pipelines", "done", "modules");

        await socket.ResendAsync("s");
        var again = await NextWith(socket, "s", "pipelines", "done", "modules");

        await Assert.That(again.GetProperty("seq").GetInt64()).IsGreaterThan(first.GetProperty("seq").GetInt64());
        await Assert.That(At(again, "pipelines", "done", "modules")!.Value.GetRawText()).IsEqualTo(At(first, "pipelines", "done", "modules")!.Value.GetRawText());
        await Assert.That(At(again, "pipelines", "done", "links")!.Value.GetRawText()).IsEqualTo(At(first, "pipelines", "done", "links")!.Value.GetRawText());
    }

    [Test]
    public async Task Cbor_carries_the_same_events_in_binary_frames()
    {
        await using var lab = await DaemonLab.StartAsync(("done.meep", Done));
        await lab.WaitFor("done", "finished");
        const string Plan = """{"list": "1h", "pipelines": {"done": {"totals": "1h", "modules": {"*": "1h"}, "links": {"*": "1h"}, "timing": {"*": "1h"}}}}""";
        await using var json = await Subscribe(lab, Plan);
        await Next(json, "s", "hello");
        var asJson = await Next(json, "s", "update");
        var jsonBytes = json.Last.Bytes;

        await using var cbor = await Subscribe(lab, Plan.Replace("{\"list\"", "{\"encoding\": \"cbor\", \"list\"", StringComparison.Ordinal));
        await Next(cbor, "s", "hello");
        await Assert.That(cbor.Last.Binary).IsTrue();
        var asCbor = await Next(cbor, "s", "update");
        await Assert.That(cbor.Last.Binary).IsTrue();

        // The same pipeline, finished, so the same values, apart from the clock and the bytes sent so far.
        await Assert.That(asCbor.GetProperty("pipelines").GetRawText()).IsEqualTo(asJson.GetProperty("pipelines").GetRawText());
        await Assert.That(asCbor.GetProperty("list").GetRawText()).IsEqualTo(asJson.GetProperty("list").GetRawText());
        await Assert.That(cbor.Last.Bytes).IsLessThan(jsonBytes);
        var done = asCbor.GetProperty("pipelines").GetProperty("done");
        await Assert.That(done.GetProperty("timing").GetProperty("m2").GetProperty("count").GetInt64()).IsEqualTo(3);
        await Assert.That(done.GetProperty("modules").GetProperty("m2")[1].GetInt64()).IsEqualTo(3);
    }

    [Test]
    public async Task A_subscription_over_http_is_refused_with_a_way_to_make_one()
    {
        await using var lab = await DaemonLab.StartAsync();
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(lab.Options.Socket), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        using var http = new HttpClient(handler);
        using var response = await http.PostAsync("http://meep/subscribe", new StringContent("""{"list": "1s"}""", Encoding.UTF8, "application/json"));
        await Assert.That((int)response.StatusCode).IsEqualTo(400);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("/ws");
    }

    [Test]
    public async Task A_budget_holds_the_bytes_sent_to_it_on_average()
    {
        await using var lab = await DaemonLab.StartAsync(("tick.meep", Tick));
        await lab.WaitFor("tick", "running");
        await using var socket = await Subscribe(lab, """{"budget": 1000, "pipelines": {"tick": {"modules": {"*": "50ms"}, "timing": {"*": "50ms"}}}}""");
        await Next(socket, "s", "hello");
        var first = await Next(socket, "s", "update");
        var started = Environment.TickCount64;
        JsonElement last;
        var held = 0L;
        do
        {
            last = await Next(socket, "s", "update");
            if (last.TryGetProperty("held", out var waited))
                held = Math.Max(held, waited.GetInt64());
        }
        while (Environment.TickCount64 - started < 3000);
        var seconds = (Environment.TickCount64 - started) / 1000.0;
        var bytes = last.GetProperty("bytes").GetInt64() - first.GetProperty("bytes").GetInt64();
        // A second's worth may go at once, then no more than the budget a second, give or take one update.
        await Assert.That(bytes).IsLessThanOrEqualTo((long)(1000 * (seconds + 1)) + socket.Last.Bytes);
        await Assert.That(bytes).IsGreaterThan(0);
        // Updates the budget held back say how long they waited, in microseconds, so the client can lower its rates.
        await Assert.That(held).IsGreaterThan(0);
        await Assert.That(held).IsLessThan(3_000_000);
    }

    [Test]
    public async Task Bytes_through_a_listen_are_counted_for_it_and_its_address_is_known()
    {
        var port = TestServer.FreePort();
        await using var lab = await DaemonLab.StartAsync(("site.meep", $$"""
            Listen "http://127.0.0.1:{{port}}/b/"
                Send text="hello"
            """));
        // Said once the Listen has bound its port, which may be just after the pipeline is running.
        await Wait.Until(async () => (await lab.Client.InfoAsync()).Listening is [_]);
        using var http = new HttpClient();
        for (var i = 0; i < 2; i++)
        {
            using var response = await http.PostAsync($"http://127.0.0.1:{port}/b/", new StringContent("abc"));
            await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("hello");
        }

        var info = await lab.Client.InfoAsync();
        await Assert.That(info.Listening!.Select(l => (l.Pipeline, l.Module, l.Address))).IsEquivalentTo([("site", "m1", $"http://127.0.0.1:{port}/b/")]);

        await using var socket = await Subscribe(lab, """{"host": "1h", "pipelines": {"site": {"totals": "1h", "bytes": {"*": "100ms"}}}}""");
        var update = await NextWith(socket, "s", "pipelines", "site", "bytes");
        // The Listen read both bodies, and wrote both replies, though Send made them; Send counts nothing of its own.
        var bytes = At(update, "pipelines", "site", "bytes")!.Value;
        await Assert.That(bytes.EnumerateObject().Select(b => b.Name)).IsEquivalentTo(["m1"]);
        await Assert.That(bytes.GetProperty("m1")[0].GetInt64()).IsEqualTo(6);
        await Assert.That(bytes.GetProperty("m1")[1].GetInt64()).IsEqualTo(10);
        await Assert.That(At(update, "pipelines", "site", "totals", "bytesSent")!.Value.GetInt64()).IsEqualTo(10);
        var listens = At(update, "host", "listens")!.Value;
        await Assert.That(listens[0].GetProperty("address").GetString()).IsEqualTo($"http://127.0.0.1:{port}/b/");
    }

    [Test]
    public async Task A_daemon_keeps_its_id_and_says_where_it_is()
    {
        using var home = new TemporaryFolder();
        var id = HostIdentity.IdOf(home.Path);
        await Assert.That(Guid.TryParse(id, out _)).IsTrue();
        await Assert.That(HostIdentity.IdOf(home.Path)).IsEqualTo(id);
        await Assert.That(File.ReadAllLines(HostIdentity.IdFile(home.Path))[0]).IsEqualTo(id);
        if (OperatingSystem.IsMacOS())
        {
            var machine = HostIdentity.MachineId();
            await Assert.That(machine).IsNotNull();
            await Assert.That(HostIdentity.MachineId()).IsEqualTo(machine);
            await Assert.That(File.ReadAllText(HostIdentity.IdFile(home.Path))).Contains($"machine {machine}");
        }

        await using var lab = await DaemonLab.StartAsync();
        var info = await lab.Client.InfoAsync();
        await Assert.That(info.Id).IsEqualTo(HostIdentity.IdOf(lab.Home));
        await Assert.That(info.Names!).Contains(Environment.MachineName);
        var clock = DateTimeOffset.UnixEpoch.AddTicks(info.Clock * 10);
        await Assert.That((DateTimeOffset.UtcNow - clock).Duration()).IsLessThan(TimeSpan.FromMinutes(1));
    }

    [Test]
    public async Task A_daemon_copied_to_another_machine_takes_an_id_of_its_own()
    {
        using var home = new TemporaryFolder();
        var id = HostIdentity.IdOf(home.Path, "aaaa");
        await Assert.That(HostIdentity.IdOf(home.Path, "aaaa")).IsEqualTo(id);

        var moved = HostIdentity.IdOf(home.Path, "bbbb");
        await Assert.That(moved).IsNotEqualTo(id);
        await Assert.That(Guid.TryParse(moved, out _)).IsTrue();
        await Assert.That(HostIdentity.IdOf(home.Path, "bbbb")).IsEqualTo(moved);
        await Assert.That(File.ReadAllText(HostIdentity.IdFile(home.Path))).Contains("machine bbbb");

        // Another copy of the same folder, started on that machine at the same time, agrees on the new ID.
        using var copy = new TemporaryFolder();
        Directory.CreateDirectory(Path.Combine(copy.Path, "daemon"));
        File.WriteAllText(HostIdentity.IdFile(copy.Path), $"{id}\nmachine aaaa\n");
        await Assert.That(HostIdentity.IdOf(copy.Path, "bbbb")).IsEqualTo(moved);
    }

    [Test]
    public async Task Without_a_machine_id_to_compare_a_daemon_keeps_its_id()
    {
        using var home = new TemporaryFolder();
        var id = HostIdentity.IdOf(home.Path, "aaaa");
        var kept = File.ReadAllText(HostIdentity.IdFile(home.Path));
        await Assert.That(HostIdentity.IdOf(home.Path, null)).IsEqualTo(id);
        await Assert.That(File.ReadAllText(HostIdentity.IdFile(home.Path))).IsEqualTo(kept);

        // One kept before the machine was: it's taken to be this machine's, and the machine is kept with it.
        using var older = new TemporaryFolder();
        Directory.CreateDirectory(Path.Combine(older.Path, "daemon"));
        File.WriteAllText(HostIdentity.IdFile(older.Path), $"{id}\n");
        await Assert.That(HostIdentity.IdOf(older.Path, "cccc")).IsEqualTo(id);
        await Assert.That(File.ReadAllText(HostIdentity.IdFile(older.Path))).Contains("machine cccc");

        // And one made where there's no machine ID is the ID alone.
        using var bare = new TemporaryFolder();
        var made = HostIdentity.IdOf(bare.Path, null);
        await Assert.That(File.ReadAllText(HostIdentity.IdFile(bare.Path))).IsEqualTo($"{made}\n");
    }

    [Test]
    public async Task Handling_times_go_in_opentelemetrys_buckets()
    {
        // Bucket i holds times above 2^(i/8) and up to 2^((i+1)/8) seconds: a power of two is the top of the bucket below.
        await Assert.That(TimingRecorder.IndexOf(1.0)).IsEqualTo(-1);
        await Assert.That(TimingRecorder.IndexOf(2.0)).IsEqualTo(7);
        await Assert.That(TimingRecorder.IndexOf(0.5)).IsEqualTo(-9);
        await Assert.That(TimingRecorder.IndexOf(1.05)).IsEqualTo(0);
        await Assert.That(TimingRecorder.IndexOf(Math.Pow(2, 1 / 8.0) * 1.0001)).IsEqualTo(1);
        await Assert.That(TimingRecorder.IndexOf(0.001)).IsEqualTo((int)Math.Ceiling(Math.Log2(0.001) * 8) - 1);

        // The same as OpenTelemetry's logarithm, away from the edges of buckets, where rounding may go either way.
        var random = new Random(202);
        for (var i = 0; i < 10_000; i++)
        {
            var seconds = Math.Pow(2, random.NextDouble() * 50 - 35);
            var exact = Math.Log2(seconds) * 8;
            if (Math.Abs(exact - Math.Round(exact)) < 1e-9)
                continue;
            await Assert.That(TimingRecorder.IndexOf(seconds)).IsEqualTo((int)Math.Ceiling(exact) - 1);
        }

        var recorder = new TimingRecorder();
        foreach (var ms in new[] { 1, 1, 2, 4, 0 })
            recorder.Record(ms * System.Diagnostics.Stopwatch.Frequency / 1000);
        var times = recorder.Snapshot();
        await Assert.That(times.Count).IsEqualTo(5);
        await Assert.That(times.Zero).IsEqualTo(1);
        await Assert.That(times.Sum).IsEqualTo(0.008).Within(1e-9);
        await Assert.That(times.Min).IsEqualTo(0.0);
        await Assert.That(times.Max).IsEqualTo(0.004).Within(1e-9);
        await Assert.That(times.Buckets.Sum()).IsEqualTo(4);
        await Assert.That(times.Offset).IsEqualTo(TimingRecorder.IndexOf(0.001));
        await Assert.That(times.Buckets[0]).IsEqualTo(2);
        await Assert.That(times.Buckets[^1]).IsEqualTo(1);
    }

    [Test]
    public async Task Metrics_and_spans_carry_each_modules_key()
    {
        var keys = new ConcurrentDictionary<string, string>();
        string? measured = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == MeepTelemetry.Name)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var named = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            if (measured is not null && (string?)named.GetValueOrDefault("meep.pipeline") == measured && named.GetValueOrDefault("meep.module.key") is string key)
                keys[(string)named["meep.module"]!] = key;
        });
        listener.Start();

        var (live, diagnostics) = LivePipeline.Load(Script.Parse("""
            Interval 5ms
                Where msg.Count > 1
                    WriteLine "{msg.Count}"
            """), new ModuleRegistry().AddAssembly(CoreModules.Assembly));
        await Assert.That(diagnostics.Where(d => d.IsError)).IsEmpty();
        await using var pipeline = live!.Pipeline;
        measured = pipeline.Name;
        var running = pipeline.RunAsync();
        await Wait.Until(() => pipeline.GetStats(pipeline.Modules[2]).Handled > 0);
        listener.RecordObservableInstruments();
        pipeline.Stop();
        await running.WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(keys.OrderBy(k => k.Value).Select(k => k.Value)).IsEquivalentTo(["m1", "m2", "m3"]);
    }
}
