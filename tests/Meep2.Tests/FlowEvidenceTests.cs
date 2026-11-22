using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Text.Json;
using Meep2.Daemon;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.Runtime.Internal;
using Meep2.SDK.Modules;
using Meep2.Tests.Support;

namespace Meep2.Tests;

/// <summary>
/// Evidence of flows between pipelines, and of the endpoints outside Meep (prompt 7): origins on what Meep sends to
/// Meep, a Listen's arrivals by origin, a Fetch's or Send's destinations by endpoint, and declared destinations.
/// </summary>
public class FlowEvidenceTests
{
    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<DaemonSocket> Subscribe(DaemonLab lab, string plan)
    {
        var socket = await lab.Client.OpenSocketAsync();
        await socket.SendAsync("s", "POST", "/subscribe", Json(plan));
        return socket;
    }

    // The next update in which a test holds for the value at a path.
    private static async Task<JsonElement> Until(DaemonSocket socket, Func<JsonElement, bool> holds, params string[] path)
    {
        var deadline = DateTime.UtcNow + Wait.Limit;
        JsonElement? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await Wait.For(socket.ReceiveAsync()) ?? throw new InvalidOperationException("The socket closed.");
            if (frame.GetProperty("event").GetString() != "update")
                continue;
            var element = frame.GetProperty("data");
            var found = true;
            foreach (var part in path)
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out element))
                {
                    found = false;
                    break;
                }
            }
            if (found)
            {
                last = element;
                if (holds(element))
                    return element;
            }
        }
        throw new TimeoutException($"Nothing at {string.Join('.', path)} held; the last was {last}.");
    }

    private static long[] Counts(JsonElement array) => [.. array.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Null ? -1 : c.GetInt64())];

    private static async Task Listening(DaemonLab lab, int count = 1) =>
        await Wait.Until(async () => (await lab.Client.InfoAsync()).Listening?.Count == count);

    [Test]
    public async Task Messages_between_two_daemons_are_counted_at_both_ends_by_each_others_ids()
    {
        var port = TestServer.FreePort();
        await using var site = await DaemonLab.StartAsync(("flow-site.meep", $$"""
            Listen "http://127.0.0.1:{{port}}/in/"
                Send text="ok"
            """));
        await Listening(site);
        await using var feeder = await DaemonLab.StartAsync();
        await feeder.Client.StartTextAsync("flow-feeder", $$"""
            Interval 20ms count=3
                Fetch "http://127.0.0.1:{{port}}/in/"
            """);
        await feeder.WaitFor("flow-feeder", "finished");

        // The Listen counts each request by where it came from: the Fetch, by its daemon, pipeline and key.
        await using (var socket = await Subscribe(site, """{"pipelines": {"flow-site": {"arrivals": {"*": "100ms"}}}}"""))
        {
            var arrivals = await Until(socket, a => a.TryGetProperty("m1", out _), "pipelines", "flow-site", "arrivals");
            var places = arrivals.GetProperty("m1");
            var origin = $"meep://{feeder.Daemon.Id}/flow-feeder/m2";
            await Assert.That(places.EnumerateObject().Select(p => p.Name)).IsEquivalentTo([origin]);
            await Assert.That(Counts(places.GetProperty(origin))).IsEquivalentTo(new long[] { 3, 3, 0, 6, 0, 0 });
        }

        // The Fetch counts each request by where it went, and knows the Listen's daemon is Meep, by its ID.
        await using (var socket = await Subscribe(feeder, """{"pipelines": {"flow-feeder": {"destinations": {"*": "100ms"}}}}"""))
        {
            var pipeline = await Until(socket, p => p.TryGetProperty("destinations", out _), "pipelines", "flow-feeder");
            var endpoint = $"127.0.0.1:{port}";
            var places = pipeline.GetProperty("destinations").GetProperty("m2");
            await Assert.That(Counts(places.GetProperty(endpoint))).IsEquivalentTo(new long[] { 3, 3, 6, 0, 0, 0 });
            await Assert.That(pipeline.GetProperty("peers").GetProperty(endpoint).GetString()).IsEqualTo(site.Daemon.Id);
        }
    }

    [Test]
    public async Task Origins_go_only_to_hosts_that_say_they_are_Meep_and_are_remembered()
    {
        var plain = new ConcurrentQueue<(string? Ask, string? Baggage)>();
        await using var outside = new TestServer(async context =>
        {
            plain.Enqueue((context.Request.Headers[Peers.HostHeader], context.Request.Headers["baggage"]));
            await TestServer.Reply(context, 200, "{}");
        });
        var meep = new ConcurrentQueue<(string? Ask, string? Baggage)>();
        await using var inside = new TestServer(async context =>
        {
            meep.Enqueue((context.Request.Headers[Peers.HostHeader], context.Request.Headers["baggage"]));
            context.Response.Headers[Peers.HostHeader] = "another-meep";
            await TestServer.Reply(context, 200, "{}");
        });

        await using var lab = await DaemonLab.StartAsync();
        await lab.Client.StartTextAsync("origins", $$"""
            Interval 10ms count=2
                Fetch "{{outside.Url}}/x"
                Fetch "{{inside.Url}}/y"
            """);
        await lab.WaitFor("origins", "finished");

        // Anywhere not known to be Meep is asked, and never told the origin.
        await Assert.That(plain.Select(p => $"{p.Ask}|{p.Baggage}")).IsEquivalentTo(["?|", "?|"]);
        // A host that answers is told the origin from then on. The first request, before the answer, only asks.
        var told = meep.ToArray();
        await Assert.That($"{told[0].Ask}|{told[0].Baggage}").IsEqualTo("?|");
        await Assert.That(told[1].Baggage).IsEqualTo($"meep.origin={lab.Daemon.Id}/origins/m3");

        await using var socket = await Subscribe(lab, """{"pipelines": {"origins": {"destinations": {"*": "100ms"}}}}""");
        var pipeline = await Until(socket, p => p.TryGetProperty("peers", out _), "pipelines", "origins");
        var peers = pipeline.GetProperty("peers");
        var insideEndpoint = new Uri(inside.Url).Authority;
        await Assert.That(peers.EnumerateObject().Select(p => (p.Name, p.Value.GetString()))).IsEquivalentTo([(insideEndpoint, (string?)"another-meep")]);
        await Assert.That(Counts(pipeline.GetProperty("destinations").GetProperty("m2").GetProperty(new Uri(outside.Url).Authority))).IsEquivalentTo(new long[] { 2, 2, 4, 0, 0, 0 });

        // The daemon remembers what it learned in its folder, for after a restart (Q213).
        MeepHosts.Save();
        await Assert.That(File.ReadAllLines(lab.Options.MeepHostsFile)).Contains($"{insideEndpoint} another-meep");
        MeepHosts.Forget(insideEndpoint);
    }

    [Test]
    public async Task Learned_hosts_are_kept_across_restarts_and_forgotten_when_they_stop_saying_so()
    {
        using var folder = new TemporaryFolder();
        var file = Path.Combine(folder.Path, "meep-hosts");
        var port = TestServer.FreePort();
        var endpoint = $"192.0.2.7:{port}";
        File.WriteAllLines(file, [$"{endpoint} kept-meep", "not a line", "192.0.2.8:1 " + new string('x', 80)]);
        using (MeepHosts.Keep(file))
        {
            await Assert.That(MeepHosts.IdOf(endpoint, "192.0.2.7", port)).IsEqualTo("kept-meep");
            await Assert.That(MeepHosts.IdOf("192.0.2.8:1", "192.0.2.8", 1)).IsNull();

            // A reply to a request that carried an origin, without Meep-Host, forgets it, here and in the file.
            MeepHosts.Answered(endpoint, (string?)null, MeepHosts.Marked.Origin);
            await Assert.That(MeepHosts.IdOf(endpoint, "192.0.2.7", port)).IsNull();
            MeepHosts.Save();
            await Assert.That(File.ReadAllText(file)).DoesNotContain(endpoint);
        }
    }

    [Test]
    [Arguments("127.0.0.1", true)]
    [Arguments("::1", true)]
    [Arguments("10.1.2.3", true)]
    [Arguments("172.16.0.1", true)]
    [Arguments("172.31.255.255", true)]
    [Arguments("172.32.0.1", false)]
    [Arguments("192.168.1.30", true)]
    [Arguments("169.254.10.10", true)]
    [Arguments("100.64.0.1", false)] // carriers' shared range (Q231)
    [Arguments("fd00::5", true)]
    [Arguments("fe80::1", true)]
    [Arguments("::ffff:192.168.1.30", true)]
    [Arguments("8.8.8.8", false)]
    [Arguments("192.0.2.7", false)]
    [Arguments("2001:db8::1", false)]
    public async Task Private_addresses_are_this_machine_private_ranges_and_link_local(string address, bool isPrivate)
    {
        await Assert.That(MeepHosts.IsPrivate(IPAddress.Parse(address))).IsEqualTo(isPrivate);
    }

    [Test]
    public async Task Only_named_local_and_private_hosts_are_asked_or_told_where_a_message_comes_from()
    {
        MeepHosts.Configure(["meep-test-public.example"]);

        await Assert.That(MeepHosts.MayTell("8.8.8.8:443", "8.8.8.8")).IsFalse();
        await Assert.That(MeepHosts.MayTell("[2001:db8::1]:443", "[2001:db8::1]")).IsFalse();
        await Assert.That(MeepHosts.MayTell("meep-test-elsewhere.invalid:443", "meep-test-elsewhere.invalid")).IsFalse();
        await Assert.That(MeepHosts.MayTell("192.168.1.30:8080", "192.168.1.30")).IsTrue();
        await Assert.That(MeepHosts.MayTell("127.0.0.1:8080", "127.0.0.1")).IsTrue();
        await Assert.That(MeepHosts.MayTell("localhost:8080", "localhost")).IsTrue();
        await Assert.That(MeepHosts.MayTell("meep-test-public.example:443", "meep-test-public.example")).IsTrue();
        await Assert.That(MeepHosts.MayTell(null, "127.0.0.1")).IsFalse();
    }

    [Test]
    [NotInParallel("meep-host-public")]
    public async Task Listens_tell_only_private_clients_the_daemons_id_unless_the_setting_says_anyone()
    {
        var before = MeepHosts.AnswerAnyone;
        try
        {
            MeepHosts.AnswerAnyone = false;
            await Assert.That(MeepHosts.MayAnswer(IPAddress.Parse("8.8.8.8"))).IsFalse();
            await Assert.That(MeepHosts.MayAnswer(IPAddress.Parse("192.168.1.30"))).IsTrue();
            await Assert.That(MeepHosts.MayAnswer(IPAddress.Loopback)).IsTrue();
            MeepHosts.AnswerAnyone = true;
            await Assert.That(MeepHosts.MayAnswer(IPAddress.Parse("8.8.8.8"))).IsTrue();
        }
        finally
        {
            MeepHosts.AnswerAnyone = before;
        }

        using var settings = new TemporaryFolder();
        var open = settings.Write("open.meep", "defaults\n    meep-host-public = true\n");
        var bad = settings.Write("bad.meep", "defaults\n    meep-host-public = \"yes\"\n");
        await Assert.That(DaemonConfig.Load(open).MeepHostPublic).IsTrue();
        await Assert.That(new DaemonConfig().MeepHostPublic).IsFalse();
        await Assert.That(() => DaemonConfig.Load(bad)).Throws<FormatException>().WithMessageContaining("meep-host-public is true or false");
    }

    [Test]
    public async Task Hosts_the_settings_name_are_told_the_origin_from_the_first_request()
    {
        var heard = new ConcurrentQueue<string?>();
        await using var named = new TestServer(async context =>
        {
            heard.Enqueue(context.Request.Headers["baggage"]);
            await TestServer.Reply(context, 200, "{}");
        });
        var endpoint = new Uri(named.Url).Authority;
        await using var lab = await DaemonLab.StartAsync(options => options with { MeepHosts = [endpoint] });
        await lab.Client.StartTextAsync("named", $$"""
            Interval 10ms count=2
                Fetch "{{named.Url}}/x"
            """);
        await lab.WaitFor("named", "finished");
        // Named, so told from the first, and still told though it never says Meep-Host.
        await Assert.That(heard.Select(h => h ?? "")).IsEquivalentTo([$"meep.origin={lab.Daemon.Id}/named/m2", $"meep.origin={lab.Daemon.Id}/named/m2"]);

        using var settings = new TemporaryFolder();
        var good = settings.Write("good.meep", "defaults\n    meep-hosts = \"Studio.local, 10.0.0.5:8080, [fd00::5]:8080\"\n");
        var bad = settings.Write("bad.meep", "defaults\n    meep-hosts = \"studio.local/x\"\n");
        await Assert.That(DaemonConfig.Load(good).MeepHosts).IsEquivalentTo(["Studio.local", "10.0.0.5:8080", "[fd00::5]:8080"]);
        await Assert.That(() => DaemonConfig.Load(bad)).Throws<FormatException>().WithMessageContaining("meep-hosts is hosts known to be Meep");
        await Assert.That(new[] { "Studio.local", "10.0.0.5:8080", "[fd00::5]:8080", "fd00::5" }.Select(h => MeepHosts.Named(h) ?? ""))
            .IsEquivalentTo(["studio.local", "10.0.0.5:8080", "[fd00::5]:8080", "fd00::5"]);
    }

    [Test]
    public async Task Redirects_are_followed_without_the_origin_and_counted_where_they_lead()
    {
        var led = new ConcurrentQueue<(string Method, string? Ask, string? Baggage, string Body)>();
        await using var target = new TestServer(async context =>
        {
            using var reader = new StreamReader(context.Request.InputStream);
            led.Enqueue((context.Request.HttpMethod, context.Request.Headers[Peers.HostHeader], context.Request.Headers["baggage"], await reader.ReadToEndAsync()));
            await TestServer.Reply(context, 200, "{}");
        });
        var first = new ConcurrentQueue<string?>();
        await using var redirecting = new TestServer(context =>
        {
            first.Enqueue(context.Request.Headers["baggage"]);
            context.Response.Headers[Peers.HostHeader] = "redirecting-meep";
            context.Response.StatusCode = context.Request.Url!.AbsolutePath switch { "/keep" => 307, "/see" => 303, _ => 302 };
            context.Response.RedirectLocation = $"{target.Url}/there";
            context.Response.Close();
            return Task.CompletedTask;
        });

        await using var lab = await DaemonLab.StartAsync();
        await lab.Client.StartTextAsync("redirected", $$"""
            Interval 10ms count=2
                Fetch "{{redirecting.Url}}/moved"
            """);
        await lab.WaitFor("redirected", "finished");
        // The Meep host that redirects is told the origin once it has said it's Meep; where it leads is only asked.
        await Assert.That(first.Select(f => f ?? "")).IsEquivalentTo(["", $"meep.origin={lab.Daemon.Id}/redirected/m2"]);
        await Assert.That(led.Select(l => $"{l.Method}|{l.Ask}|{l.Baggage}")).IsEquivalentTo(["GET|?|", "GET|?|"]);

        await using (var socket = await Subscribe(lab, """{"pipelines": {"redirected": {"destinations": {"*": "100ms"}}}}"""))
        {
            var places = await Until(socket, p => p.TryGetProperty("m2", out _), "pipelines", "redirected", "destinations");
            await Assert.That(places.GetProperty("m2").EnumerateObject().Select(p => p.Name)).IsEquivalentTo([new Uri(target.Url).Authority]);
        }

        // A 307 sends the body again, with the same method; a 303 becomes a GET without it.
        led.Clear();
        await lab.Client.StartTextAsync("posted", $$"""
            Interval 10ms count=1
                Send "{{redirecting.Url}}/keep" text="kept"
                Send "{{redirecting.Url}}/see" text="seen"
            """);
        await Wait.Until(() => led.Count == 2);
        await Assert.That(led.Select(l => $"{l.Method}|{l.Body}")).IsEquivalentTo(["POST|kept", "GET|"]);
        MeepHosts.Forget(new Uri(redirecting.Url).Authority);
    }

    [Test]
    [NotInParallel("tracing")]
    public async Task A_listen_on_another_daemon_continues_the_trace_and_unknown_clients_count_by_address()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == MeepTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.Parent == default || (options.Parent.TraceFlags & ActivityTraceFlags.Recorded) != 0
                    ? ActivitySamplingResult.AllDataAndRecorded
                    : ActivitySamplingResult.PropagationData,
            ActivityStopped = activity =>
            {
                if ((string?)activity.GetTagItem("meep.pipeline") is "trace-site" or "trace-feeder")
                    spans.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);

        var port = TestServer.FreePort();
        await using var site = await DaemonLab.StartAsync(("trace-site.meep", $$"""
            Listen "http://127.0.0.1:{{port}}/t/"
                Send text="ok"
            """));
        await Listening(site);
        // As though on another machine: this process's own Listens are known to be Meep without asking.
        MeepHosts.Unbound(port, site.Daemon.Id);
        await using var feeder = await DaemonLab.StartAsync();
        await feeder.Client.StartTextAsync("trace-feeder", $$"""
            Interval 20ms count=2
                Fetch "http://127.0.0.1:{{port}}/t/"
            """);
        await feeder.WaitFor("trace-feeder", "finished");
        await Wait.Until(() => spans.Count(s => s.DisplayName == "Listen.HTTP") == 2);

        // Each request's span on the Listen's side hangs from the Fetch's span that made it.
        var fetches = spans.Where(s => s.DisplayName == "Fetch.HTTP").ToList();
        var listens = spans.Where(s => s.DisplayName == "Listen.HTTP").ToList();
        foreach (var listen in listens)
            await Assert.That(fetches.Any(f => f.TraceId == listen.TraceId && f.SpanId == listen.ParentSpanId)).IsTrue();

        // The first request asked whether the Listen is Meep, without saying where it was from, so it counts as from
        // outside, by its address, named beside it; the second says where.
        await using var socket = await Subscribe(site, """{"pipelines": {"trace-site": {"arrivals": {"*": "100ms"}}}}""");
        var pipeline = await Until(socket, p => p.TryGetProperty("arrivals", out var a) && a.GetProperty("m1").EnumerateObject().Count() == 2, "pipelines", "trace-site");
        var places = pipeline.GetProperty("arrivals").GetProperty("m1");
        await Assert.That(Counts(places.GetProperty("127.0.0.1"))[0]).IsEqualTo(1);
        await Assert.That(Counts(places.GetProperty($"meep://{feeder.Daemon.Id}/trace-feeder/m2"))[0]).IsEqualTo(1);
        await Assert.That(pipeline.GetProperty("clients").GetProperty("127.0.0.1").GetString()).IsEqualTo("localhost");
        MeepHosts.Forget($"127.0.0.1:{port}");
    }

    [Test]
    public async Task Each_send_has_a_websocket_of_its_own_to_a_Meep_host()
    {
        var port = TestServer.FreePort();
        await using var site = await DaemonLab.StartAsync(("ws-site.meep", $$"""
            Listen "ws://127.0.0.1:{{port}}/w/"
                WriteLine "got {msg.Text}"
            """));
        await Listening(site);
        MeepHosts.Unbound(port, site.Daemon.Id); // so the first connection asks, as it would another machine
        await using var feeder = await DaemonLab.StartAsync();
        await feeder.Client.StartTextAsync("ws-feeder", $$"""
            Interval 100ms count=3
                Send "ws://127.0.0.1:{{port}}/w/" text="a"
                Send "ws://127.0.0.1:{{port}}/w/" text="b"
            """);
        await Wait.Until(() => Lines(site, "got a") == 3 && Lines(site, "got b") == 3);

        // The first messages went on one connection shared while the Listen wasn't known to be Meep, counted by address.
        // Once it answered, each Send moved to a connection of its own, counted under its own origin.
        await using var socket = await Subscribe(site, """{"pipelines": {"ws-site": {"arrivals": {"*": "100ms"}}}}""");
        var arrivals = await Until(socket, a => a.TryGetProperty("m1", out var m1) && m1.EnumerateObject().Sum(p => p.Value[0].GetInt64()) == 6, "pipelines", "ws-site", "arrivals");
        var places = arrivals.GetProperty("m1").EnumerateObject().ToDictionary(p => p.Name, p => Counts(p.Value));
        var a = $"meep://{feeder.Daemon.Id}/ws-feeder/m2";
        var b = $"meep://{feeder.Daemon.Id}/ws-feeder/m3";
        await Assert.That(places.Keys.Except(["127.0.0.1", a, b])).IsEmpty();
        await Assert.That(places[a][0]).IsGreaterThanOrEqualTo(2);
        await Assert.That(places[b][0]).IsGreaterThanOrEqualTo(2);
        await Assert.That(Lines(site, "got a") + Lines(site, "got b")).IsEqualTo(6);

        await using var feed = await Subscribe(feeder, """{"pipelines": {"ws-feeder": {"destinations": {"*": "100ms"}}}}""");
        var pipeline = await Until(feed, p => p.TryGetProperty("peers", out _), "pipelines", "ws-feeder");
        await Assert.That(pipeline.GetProperty("peers").GetProperty($"127.0.0.1:{port}").GetString()).IsEqualTo(site.Daemon.Id);
        MeepHosts.Forget($"127.0.0.1:{port}");

        static int Lines(DaemonLab lab, string text) => lab.Output.ToString().Split('\n').Count(l => l.Trim() == text);
    }

    [Test]
    public async Task Replies_from_400_to_499_are_rejected_and_from_500_failed_at_both_ends()
    {
        var port = TestServer.FreePort();
        await using var site = await DaemonLab.StartAsync(("status-site.meep", $$"""
            Listen "http://127.0.0.1:{{port}}/no/"
                Send status=404 text="no"
            Listen "http://127.0.0.1:{{port}}/bad/"
                Send status=503 text="bad"
            """));
        await Listening(site, 2);
        await site.Client.StartTextAsync("status-feeder", $$"""
            Interval 10ms count=2
                Fetch "http://127.0.0.1:{{port}}/no/"
                Fetch "http://127.0.0.1:{{port}}/bad/"
            """);

        await using var socket = await Subscribe(site, """{"pipelines": {"status-site": {"arrivals": {"*": "100ms"}}, "status-feeder": {"destinations": {"*": "100ms"}}}}""");
        var origin = $"meep://{site.Daemon.Id}/status-feeder";
        var site404 = new long[] { 2, 2, 0, 4, 0, 2 };
        var site503 = new long[] { 2, 2, 0, 6, 2, 0 };
        var fetch404 = new long[] { 2, 2, 4, 0, 0, 2 };
        var fetch503 = new long[] { 2, 2, 6, 0, 2, 0 };
        long[]? seen404 = null, seen503 = null, sent404 = null, sent503 = null;
        await Until(socket, update =>
        {
            if (update.TryGetProperty("status-site", out var s) && s.TryGetProperty("arrivals", out var arrivals))
            {
                if (arrivals.TryGetProperty("m1", out var m1) && m1.TryGetProperty($"{origin}/m2", out var c1))
                    seen404 = Merge(seen404, Counts(c1));
                if (arrivals.TryGetProperty("m3", out var m3) && m3.TryGetProperty($"{origin}/m3", out var c3))
                    seen503 = Merge(seen503, Counts(c3));
            }
            if (update.TryGetProperty("status-feeder", out var f) && f.TryGetProperty("destinations", out var destinations))
            {
                if (destinations.TryGetProperty("m2", out var m2) && m2.TryGetProperty($"127.0.0.1:{port}", out var d2))
                    sent404 = Merge(sent404, Counts(d2));
                if (destinations.TryGetProperty("m3", out var m3) && m3.TryGetProperty($"127.0.0.1:{port}", out var d3))
                    sent503 = Merge(sent503, Counts(d3));
            }
            return Same(seen404, site404) && Same(seen503, site503) && Same(sent404, fetch404) && Same(sent503, fetch503);
        }, "pipelines");

        // Applies an update's counts, null (-1 here) leaving a count as it was, as a client does.
        static long[] Merge(long[]? before, long[] now)
        {
            var merged = before ?? new long[6];
            for (var i = 0; i < now.Length; i++)
            {
                if (now[i] >= 0)
                    merged[i] = now[i];
            }
            return merged;
        }

        static bool Same(long[]? seen, long[] expected) => seen is not null && seen.SequenceEqual(expected);
    }

    [Test]
    public async Task Fetches_and_sends_say_where_they_send_before_anything_runs()
    {
        // Nothing reaches them: they're only declared.
        await using var lab = await DaemonLab.StartAsync(("declared.meep", """
            Interval 1h
                Where msg.Count < 0
                    Fetch "http://studio.local:8080/hooks/"
                    Fetch "https://api.example.com/stops/{msg.Count}"
                    Fetch "~/stops.json"
                    Send "tcp://printer.local:9100"
            """));
        await lab.WaitFor("declared", "running");
        IReadOnlyList<SendInfo>? sending = null;
        await Wait.Until(async () => (sending = (await lab.Client.InfoAsync()).Sending) is { Count: 3 });
        await Assert.That(sending!.Select(s => (s.Module, s.Address, s.Computed, s.Endpoint))).IsEquivalentTo([
            ("m3", "http://studio.local:8080/hooks/", false, (string?)"studio.local:8080"),
            ("m4", "\"https://api.example.com/stops/{msg.Count}\"", true, "api.example.com:443"),
            ("m6", "tcp://printer.local:9100", false, "printer.local:9100"),
        ]);

        await using var socket = await Subscribe(lab, """{"host": "100ms"}""");
        var sends = await Until(socket, s => s.GetArrayLength() == 3, "host", "sends");
        await Assert.That(sends[1].GetProperty("computed").GetBoolean()).IsTrue();
        await Assert.That(sends[0].GetProperty("endpoint").GetString()).IsEqualTo("studio.local:8080");
    }

    [Test]
    public async Task A_module_keeps_a_bounded_number_of_places_and_counts_the_rest_together()
    {
        var table = PeerTable.ForArrivals();
        for (var i = 0; i < 70; i++)
            table.For($"10.0.0.{i}").Count(received: 1);
        for (var i = 0; i < 260; i++)
            table.For($"meep://host/p/m{i}").Count(received: 1, bytesReceived: 2);
        table.For("10.0.0.1").Count(received: 1, rejected: 1);

        // None has been quiet for ten minutes, so none is folded: the new ones are counted together.
        var read = table.Read().ToDictionary(p => p.Place);
        await Assert.That(read.Count).IsEqualTo(64 + 1 + 256 + 1);
        await Assert.That(read["*"].Received).IsEqualTo(6);
        await Assert.That(read["meep://*"].Received).IsEqualTo(4);
        await Assert.That(read["meep://*"].BytesReceived).IsEqualTo(8);
        await Assert.That(read["10.0.0.1"].Received).IsEqualTo(2);
        await Assert.That(read["10.0.0.1"].Rejected).IsEqualTo(1);
        await Assert.That(read.Values.Sum(p => p.Received)).IsEqualTo(70 + 260 + 1);
    }

    [Test]
    [NotInParallel("peer-clock")]
    public async Task A_place_quiet_for_ten_minutes_is_folded_when_a_new_one_needs_the_room()
    {
        var now = 1_000_000L;
        var clock = PeerTable.Clock;
        PeerTable.Clock = () => Volatile.Read(ref now);
        try
        {
            var table = PeerTable.ForDestinations();
            for (var i = 0; i < 64; i++)
                table.For($"host{i}:80").Count(sent: 1, bytesSent: 10);
            var held = table.For("host5:80");
            Volatile.Write(ref now, now + (long)TimeSpan.FromMinutes(9).TotalMilliseconds);
            table.For("host7:80").Count(sent: 1); // busy again

            // Nothing quiet for ten minutes yet: a new place goes under *.
            table.For("new1:80").Count(sent: 1);
            await Assert.That(table.Read().Select(p => p.Place)).DoesNotContain("new1:80");

            // Later, the quietest is folded for the next new one, and a tally kept for it counts into * too.
            Volatile.Write(ref now, now + (long)TimeSpan.FromMinutes(2).TotalMilliseconds);
            table.For("new2:80").Count(sent: 1);
            held.Count(sent: 1);
            var read = table.Read().ToDictionary(p => p.Place);
            await Assert.That(read.ContainsKey("new2:80")).IsTrue();
            await Assert.That(read.ContainsKey("host7:80")).IsTrue();
            await Assert.That(read.Count).IsEqualTo(64 + 1);
            await Assert.That(read.Values.Sum(p => p.Sent)).IsEqualTo(64 + 1 + 1 + 1 + 1);
            await Assert.That(read.Values.Sum(p => p.BytesSent)).IsEqualTo(640);
            await Assert.That(read["*"].Sent).IsGreaterThanOrEqualTo(2);
        }
        finally
        {
            PeerTable.Clock = clock;
        }
    }

    [Test]
    [NotInParallel("peer-clock")]
    public async Task A_subscription_says_which_places_were_folded()
    {
        var now = 1_000_000L;
        var clock = PeerTable.Clock;
        PeerTable.Clock = () => Volatile.Read(ref now);
        try
        {
            var port = TestServer.FreePort();
            await using var site = await DaemonLab.StartAsync(("fold-site.meep", $$"""
                Listen "http://127.0.0.1:{{port}}/f/"
                    Send text="ok"
                """));
            await Listening(site);
            using var http = new HttpClient();
            async Task From(string pipeline)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/f/");
                request.Headers.Add("baggage", $"meep.origin=elsewhere/{pipeline}/m1");
                using var response = await http.SendAsync(request);
            }
            for (var i = 0; i < 256; i++)
                await From($"p{i}");

            await using var socket = await Subscribe(site, """{"pipelines": {"fold-site": {"arrivals": {"*": "100ms"}}}}""");
            await Until(socket, a => a.TryGetProperty("m1", out var m1) && m1.EnumerateObject().Count() == 256 && m1.EnumerateObject().All(p => p.Value[1].GetInt64() == 1), "pipelines", "fold-site", "arrivals");

            Volatile.Write(ref now, now + (long)TimeSpan.FromMinutes(11).TotalMilliseconds);
            await From("p256");
            var pipeline = await Until(socket, p => p.TryGetProperty("folded", out _), "pipelines", "fold-site");
            var folded = pipeline.GetProperty("folded").GetProperty("m1").EnumerateArray().Select(f => f.GetString()!).ToList();
            await Assert.That(folded.Count).IsEqualTo(1);
            var places = pipeline.GetProperty("arrivals").GetProperty("m1");
            await Assert.That(places.GetProperty(folded[0]).ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(Counts(places.GetProperty("meep://elsewhere/p256/m1"))[0]).IsEqualTo(1);
            await Assert.That(Counts(places.GetProperty("meep://*"))[0]).IsEqualTo(1);
        }
        finally
        {
            PeerTable.Clock = clock;
        }
    }

    [Test]
    public async Task A_response_body_is_read_whole_whether_or_not_its_length_is_given()
    {
        var bytes = new byte[200_000];
        Random.Shared.NextBytes(bytes);
        using var known = new ByteArrayContent(bytes[..10]);
        await Assert.That(await HttpFetch.ReadBodyAsync(known, "known", 100, CancellationToken.None)).IsEquivalentTo(bytes[..10]);
        using var unknown = new StreamContent(PipeReader.Create(new MemoryStream(bytes)).AsStream());
        await Assert.That(unknown.Headers.ContentLength).IsNull();
        await Assert.That(await HttpFetch.ReadBodyAsync(unknown, "unknown", 1_000_000, CancellationToken.None)).IsEquivalentTo(bytes);
        using var large = new StreamContent(PipeReader.Create(new MemoryStream(bytes)).AsStream());
        await Assert.That(async () => await HttpFetch.ReadBodyAsync(large, "large", 100_000, CancellationToken.None)).Throws<HttpFetch.TooLargeException>();
    }

    [Test]
    public async Task A_response_becomes_a_response_or_an_error_by_its_status()
    {
        using var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("caf\u00e9", System.Text.Encoding.Latin1, "text/plain") };
        var body = await ok.Content.ReadAsByteArrayAsync();
        var response = HttpFetch.MessageFor("http://a.example/", ok, body) as HttpResponse;
        await Assert.That(response).IsNotNull();
        await Assert.That(response!.Text).IsEqualTo("caf\u00e9");
        await Assert.That(response.StatusCode).IsEqualTo(200);
        await Assert.That(response.ContentType).IsEqualTo("text/plain");

        using var missing = new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = "Not Found", Content = new StringContent("no") };
        var error = HttpFetch.MessageFor("http://a.example/x", missing, await missing.Content.ReadAsByteArrayAsync()) as HttpErrorMessage;
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Error).IsEqualTo("http://a.example/x answered 404 Not Found");
        await Assert.That(error.StatusCode).IsEqualTo(404);
        await Assert.That(error.Text).IsEqualTo("no");
    }

    [Test]
    public async Task Origins_endpoints_and_clients_are_written_one_way()
    {
        var origin = Peers.Origin("01d2", "my site", "m3");
        await Assert.That(origin).IsEqualTo("01d2/my%20site/m3");
        await Assert.That(Peers.OriginIn($"other=1, meep.origin={origin};prop, more=2")).IsEqualTo(origin);
        await Assert.That(Peers.OriginIn("meep.origin=a/b")).IsNull();
        await Assert.That(Peers.OriginIn("meep.origin=a//c")).IsNull();
        await Assert.That(Peers.OriginIn("meep.origin=a/b c/d")).IsNull();
        await Assert.That(Peers.OriginIn("meep.origin=" + new string('a', 300) + "/b/c")).IsNull();

        await Assert.That(Peers.EndpointOf("https://API.example.com/x")).IsEqualTo("api.example.com:443");
        await Assert.That(Peers.EndpointOf("ws://[::1]:8080/w/")).IsEqualTo("[::1]:8080");
        await Assert.That(Peers.EndpointOf("tcp://printer.local:9100")).IsEqualTo("printer.local:9100");
        await Assert.That(Peers.EndpointOf("~/stops.json")).IsNull();
        await Assert.That(Peers.EndpointOfTemplate("\"https://api.example.com/stops/{msg.Id}\"")).IsEqualTo("api.example.com:443");
        await Assert.That(Peers.EndpointOfTemplate("\"https://{msg.Host}/stops/\"")).IsNull();
        await Assert.That(Peers.EndpointOfTemplate("\"http://a.example:{msg.Port}/\"")).IsNull();
        await Assert.That(Peers.EndpointOfTemplate("msg.Url")).IsNull();

        await Assert.That(Peers.ClientOf(IPAddress.Parse("::ffff:192.168.1.30"))).IsEqualTo("192.168.1.30");
    }
}
