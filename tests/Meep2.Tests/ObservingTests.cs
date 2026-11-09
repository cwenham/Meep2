using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Meep2.Daemon;
using Meep2.Meeplang.Compilation;
using Meep2.Modules;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.Tests.Support;
using Numbers = Meep2.Tests.Support.Numbers;

namespace Meep2.Tests;

/// <summary>Watching a running pipeline: taps, injected messages, OpenTelemetry, and messages as JSON (step 7).</summary>
public class ObservingTests
{
    [Test]
    public async Task A_tap_sees_what_a_module_emits_and_what_it_passes_on()
    {
        var builder = new PipelineBuilder("tapped");
        var source = builder.AddSource(() => new Numbers(1, 2, 3, 4));
        var double_ = builder.Add(() => new Map<Number>((number, context) => context.Emit(new Number(number.Value * 2))));
        var recorder = new Recorder<Number>();
        var passthrough = builder.Add(() => recorder);
        var sink = new Collect<Number>();
        builder.Chain(source, double_, passthrough, builder.Add(() => sink));
        await using var pipeline = builder.Build();
        using var atSource = pipeline.Tap(source);
        using var atDouble = pipeline.Tap(double_);
        using var atPassthrough = pipeline.Tap(passthrough);

        await Wait.For(pipeline.RunAsync());

        await Assert.That(await Values(atSource)).IsEquivalentTo([1, 2, 3, 4]);
        await Assert.That(await Values(atDouble)).IsEquivalentTo([2, 4, 6, 8]);
        await Assert.That(await Values(atPassthrough)).IsEquivalentTo([2, 4, 6, 8]);
    }

    [Test]
    public async Task A_tap_shows_only_what_its_condition_matches()
    {
        var builder = new PipelineBuilder("filtered");
        var source = builder.AddSource(() => new Numbers(1, 2, 3, 4, 5, 6));
        builder.Chain(source, builder.Add(() => new Collect<Number>()));
        await using var pipeline = builder.Build();
        using var evens = pipeline.Tap(source, new DebugTapOptions { Where = message => message is Number { Value: var n } && n % 2 == 0 });
        using var failing = pipeline.Tap(source, new DebugTapOptions { Where = _ => throw new InvalidOperationException("broken filter") });

        await Wait.For(pipeline.RunAsync());

        await Assert.That(await Values(evens)).IsEquivalentTo([2, 4, 6]);
        await Assert.That(await Values(failing)).IsEmpty();
    }

    [Test]
    public async Task A_tap_nobody_reads_drops_its_oldest_and_never_holds_the_pipeline_up()
    {
        var builder = new PipelineBuilder("ignored");
        var source = builder.AddSource(() => new Numbers([.. Enumerable.Range(1, 50)]));
        var sink = new Collect<Number>();
        builder.Chain(source, builder.Add(() => sink));
        await using var pipeline = builder.Build();
        using var tap = pipeline.Tap(source, new DebugTapOptions { BufferSize = 5 });

        await Wait.For(pipeline.RunAsync());

        await Assert.That(sink.Received.Count).IsEqualTo(50);
        await Assert.That(tap.Dropped).IsEqualTo(45);
        await Assert.That(await Values(tap)).IsEquivalentTo([46, 47, 48, 49, 50]);
    }

    [Test]
    public async Task A_closed_tap_stops_receiving_and_the_module_carries_on()
    {
        var builder = new PipelineBuilder("closing");
        var feed = new Feed();
        var source = builder.AddSource(() => feed);
        var sink = new Collect<Number>();
        builder.Chain(source, builder.Add(() => sink));
        await using var pipeline = builder.Build();
        var tap = pipeline.Tap(source);
        var running = pipeline.RunAsync();

        await feed.Send(new Number(1));
        tap.Dispose();
        await feed.Send(new Number(2));
        feed.Finish();
        await Wait.For(running);

        await Assert.That(await Values(tap)).IsEquivalentTo([1]);
        await Assert.That(sink.Received.Count).IsEqualTo(2);
    }

    [Test]
    public async Task An_injected_message_reaches_a_module_as_if_from_upstream()
    {
        var builder = new PipelineBuilder("injected");
        var feed = new Feed();
        var sink = new Collect<Message>();
        var module = builder.Add(() => sink, name: "Sink");
        builder.Chain(builder.AddSource(() => feed), module);
        await using var pipeline = builder.Build();
        var running = pipeline.RunAsync();

        pipeline.Inject(module, new TextMessage("hello"));
        await Wait.For(sink.Reached(1));
        feed.Finish();
        await Wait.For(running);

        var received = (TextMessage)sink.Received.Single();
        await Assert.That(received.Text).IsEqualTo("hello");
        await Assert.That(received.EmittedBy).IsEqualTo(Pipeline.Injector);
        await Assert.That(() => pipeline.Inject(module, new TextMessage("too late"))).Throws<InvalidOperationException>();
    }

    [Test]
    [NotInParallel("tracing")]
    public async Task Each_hop_is_a_span_whose_parent_is_where_its_message_came_from()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans, "traced");
        var builder = new PipelineBuilder("traced");
        var source = builder.AddSource(() => new Numbers(1, 2), name: "Source");
        var doubler = builder.Add(() => new Map<Number>((number, context) => context.Emit(new Number(number.Value * 2))), name: "Doubler");
        var thrower = builder.Add(() => new Map<Number>((number, _) => throw new InvalidOperationException($"no {number.Value}")), name: "Thrower");
        builder.Chain(source, doubler, thrower);
        await using var pipeline = builder.Build();

        await Wait.For(pipeline.RunAsync());

        var emitted = spans.Where(s => s.Kind == ActivityKind.Producer).ToList();
        var doubled = spans.Where(s => (string?)s.GetTagItem("meep.module") == "Doubler").ToList();
        var thrown = spans.Where(s => (string?)s.GetTagItem("meep.module") == "Thrower").ToList();
        await Assert.That(emitted.Count).IsEqualTo(2);
        await Assert.That(doubled.Count).IsEqualTo(2);
        await Assert.That(thrown.Count).IsEqualTo(2);
        foreach (var span in doubled)
            await Assert.That(emitted.Select(e => e.SpanId)).Contains(span.ParentSpanId);
        foreach (var span in thrown)
        {
            await Assert.That(doubled.Select(d => d.SpanId)).Contains(span.ParentSpanId);
            await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
        }
        await Assert.That(thrown.Select(s => s.TraceId).Distinct().Count()).IsEqualTo(2); // one trace per number
        await Assert.That(doubled[0].DisplayName).IsEqualTo("Map");
    }

    [Test]
    public async Task Metrics_come_from_each_modules_counts()
    {
        var measured = new ConcurrentQueue<(string Instrument, long Value, Dictionary<string, object?> Tags)>();
        var durations = new ConcurrentQueue<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == MeepTelemetry.Name)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var named = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            if (named.TryGetValue("meep.pipeline", out var pipelineName) && (string?)pipelineName == "measured")
                measured.Enqueue((instrument.Name, value, named));
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "meep.module.duration" && tags.ToArray().Any(t => t.Key == "meep.pipeline" && (string?)t.Value == "measured"))
                durations.Enqueue(value);
        });
        listener.Start();

        var builder = new PipelineBuilder("measured");
        var feed = new Feed();
        var sink = new Collect<Number>();
        builder.Chain(builder.AddSource(() => feed), builder.Add(() => sink, name: "Sink"));
        await using var pipeline = builder.Build();
        var running = pipeline.RunAsync();
        for (var i = 0; i < 3; i++)
            await feed.Send(new Number(i));
        await Wait.For(sink.Reached(3));
        listener.RecordObservableInstruments();
        feed.Finish();
        await Wait.For(running);

        var handled = measured.Where(m => m.Instrument == "meep.module.messages" && (string?)m.Tags["meep.module"] == "Sink" && (string?)m.Tags["meep.event"] == "handled").ToList();
        await Assert.That(handled.Count).IsEqualTo(1);
        await Assert.That(handled[0].Value).IsEqualTo(3);
        await Assert.That(measured.Any(m => m.Instrument == "meep.module.queued")).IsTrue();
        await Assert.That(durations.Count).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task A_message_is_shown_as_json_with_its_content_leases_and_fam()
    {
        var builder = new PipelineBuilder("shown");
        var feed = new Feed();
        var source = builder.AddSource(() => feed, name: "Feed");
        Message? shown = null;
        var make = builder.Add(() => new Map<Number>((number, context) =>
        {
            using var document = JsonDocument.Parse("""{"nested":{"list":[1,2,3]}}""");
            var json = new JsonMessage(document.RootElement.Clone());
            context.Emit(json);
            shown = json;
        }), name: "Make");
        builder.Chain(source, make, builder.Add(() => new Collect<Message>()));
        await using var pipeline = builder.Build();
        var running = pipeline.RunAsync();
        await feed.Send(new Number(7));
        feed.Finish();
        await Wait.For(running);

        using var document = JsonDocument.Parse(MessageJson.ToJson(shown!));
        var root = document.RootElement;
        await Assert.That(root.GetProperty("type").GetString()).IsEqualTo("JsonMessage");
        await Assert.That(root.GetProperty("emittedBy").GetString()).IsEqualTo("Make");
        await Assert.That(root.GetProperty("content").GetProperty("Value").GetProperty("nested").GetProperty("list")[2].GetInt32()).IsEqualTo(3);
        var fam = root.GetProperty("fam");
        await Assert.That(fam[0].GetProperty("type").GetString()).IsEqualTo("Number");
        await Assert.That(fam[0].GetProperty("emittedBy").GetString()).IsEqualTo("Feed");
    }

    [Test]
    public async Task Json_cuts_long_text_and_shows_bytes_errors_and_dictionaries_plainly()
    {
        var error = new ErrorMessage { Error = "it broke", Exception = new InvalidOperationException("deep down") };
        var long_ = new TextMessage(new string('x', 10_000));
        var response = new HttpResponse
        {
            Url = "http://example.test/",
            StatusCode = 200,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "text/plain" },
            Body = new byte[] { 1, 2, 3 },
            Text = "abc",
        };

        using var errorJson = JsonDocument.Parse(MessageJson.ToJson(error));
        using var longJson = JsonDocument.Parse(MessageJson.ToJson(long_));
        using var responseJson = JsonDocument.Parse(MessageJson.ToJson(response));

        await Assert.That(errorJson.RootElement.GetProperty("content").GetProperty("Error").GetString()).IsEqualTo("it broke");
        await Assert.That(errorJson.RootElement.GetProperty("content").GetProperty("Exception").GetProperty("message").GetString()).IsEqualTo("deep down");
        await Assert.That(longJson.RootElement.GetProperty("content").GetProperty("Text").GetString()!.Length).IsEqualTo(4097);
        await Assert.That(responseJson.RootElement.GetProperty("content").GetProperty("Body").GetString()).IsEqualTo("3 bytes");
        await Assert.That(responseJson.RootElement.GetProperty("content").GetProperty("Headers").GetProperty("Content-Type").GetString()).IsEqualTo("text/plain");
    }

    [Test]
    public async Task A_condition_fits_each_message_type_it_meets()
    {
        var registry = new Meep2.Meeplang.Loading.ModuleRegistry().AddAssembly(CoreModules.Assembly);
        var condition = MessageCondition.Parse("msg is Tick and msg.Count > 1", registry);
        var text = MessageCondition.Parse("msg.Text == \"hi\"", registry);
        var either = MessageCondition.Parse("msg is Tick and msg.Count > 1 or msg is TextMessage", registry);
        var grouped = MessageCondition.Parse("(msg is Tick and msg.Count > 1) or (msg is TextMessage)", registry);
        var notTick = MessageCondition.Parse("msg is not Tick or msg.Count > 1", registry);

        await Assert.That(condition.Matches(new Tick(1))).IsFalse();
        await Assert.That(condition.Matches(new Tick(2))).IsTrue();
        await Assert.That(condition.Matches(new TextMessage("hi"))).IsFalse();
        await Assert.That(text.Matches(new TextMessage("hi"))).IsTrue();
        await Assert.That(text.Matches(new Tick(2))).IsFalse(); // a Tick has no Text, so it doesn't fit

        // After msg is Tick, msg is a Tick for the rest of the and, so msg.Count fits a TextMessage there (Q113).
        await Assert.That(either.Matches(new TextMessage("hi"))).IsTrue();
        await Assert.That(either.Matches(new Tick(2))).IsTrue();
        await Assert.That(either.Matches(new Tick(1))).IsFalse();
        await Assert.That(grouped.Matches(new TextMessage("hi"))).IsTrue();
        await Assert.That(grouped.Matches(new Tick(1))).IsFalse();
        await Assert.That(notTick.Matches(new TextMessage("hi"))).IsTrue();
        await Assert.That(notTick.Matches(new Tick(1))).IsFalse();
        await Assert.That(notTick.Matches(new Tick(2))).IsTrue();
    }

    [Test]
    public async Task A_condition_with_a_mistake_says_what_it_is()
    {
        var registry = new Meep2.Meeplang.Loading.ModuleRegistry().AddAssembly(CoreModules.Assembly);

        await Assert.That(() => MessageCondition.Parse("msg is Tik", registry)).Throws<FormatException>().WithMessageContaining("There's no message type called Tik.");
        await Assert.That(() => MessageCondition.Parse("msg.Count >", registry)).Throws<FormatException>();
        await Assert.That(() => MessageCondition.Parse("msg.Count > 1 2", registry)).Throws<FormatException>().WithMessageContaining("Expected the condition to end");
    }

    private static async Task<int[]> Values(DebugTap tap)
    {
        var values = new List<int>();
        await foreach (var message in tap.Messages.ReadAllAsync())
            values.Add(((Number)message).Value);
        return [.. values];
    }

    private static ActivityListener Listen(ConcurrentQueue<Activity> spans, string pipeline)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == MeepTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if ((string?)activity.GetTagItem("meep.pipeline") == pipeline)
                    spans.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
