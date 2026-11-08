using System.Collections.Concurrent;
using Meep2.Runtime;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.Tests.Support;

namespace Meep2.Tests;

/// <summary>Verbs with several inflections: routing by type and by parameters (Q19, Q20), and refusals (Q54).</summary>
public class VerbTests
{
    private static Map<TIn> Says<TIn>(Func<TIn, string> words)
        where TIn : class => new((message, context) => context.Emit(new Word(words(message))));

    [Test]
    public async Task A_verb_gives_each_message_to_the_inflection_that_fits_its_type_most_closely()
    {
        var builder = new PipelineBuilder();
        var after = new Collect<Word>();
        var describe = builder.AddVerb("Describe",
        [
            new VerbInflection("Anything", () => Says<Message>(_ => "something")),
            new VerbInflection("Numbers", () => Says<Number>(n => $"number {n.Value}")),
        ]);
        builder.Chain(builder.AddSource(() => new Messages(new Number(1), new Word("x"), new Number(2))), describe, builder.Add(() => after));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(Wait.Join(after.Received.Select(w => w.Value))).IsEqualTo("number 1,something,number 2");
        var fromNumbers = after.Received[0].EmittedBy!;
        await Assert.That(fromNumbers.Inflection).IsEqualTo("Numbers");
        await Assert.That(fromNumbers.Verb).IsEqualTo("Describe");
        await Assert.That(after.Received[1].EmittedBy!.Inflection).IsEqualTo("Anything");
        await Assert.That(pipeline.GetStats(fromNumbers).Handled).IsEqualTo(3); // any inflection finds the module's counts
    }

    [Test]
    public async Task Fam_finds_an_ancestor_by_its_verb_or_its_inflection()
    {
        var builder = new PipelineBuilder();
        var after = new Collect<Word>();
        var describe = builder.AddVerb("Describe",
        [
            new VerbInflection("Anything", () => Says<Message>(_ => "something")),
            new VerbInflection("Integers", () => Says<Number>(n => $"number {n.Value}")),
        ]);
        var shout = builder.Add(() => new Map<Word>((w, context) => context.Emit(new Word(w.Value.ToUpperInvariant()))));
        builder.Chain(builder.AddSource(() => new Numbers(7)), describe, shout, builder.Add(() => after));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        var shouted = after.Received.Single();
        await Assert.That(((Word)shouted.Fam["Integers"]!).Value).IsEqualTo("number 7");
        await Assert.That(((Word)shouted.Fam["Describe"]!).Value).IsEqualTo("number 7");
        await Assert.That(shouted.Fam["Anything"]).IsNull();
    }

    [Test]
    public async Task Parameters_worked_out_per_message_choose_between_inflections_that_fit_equally_well()
    {
        var builder = new PipelineBuilder();
        var after = new Collect<Word>();
        var pick = builder.AddVerb("Pick",
        [
            new VerbInflection("Even", () => Says<Number>(n => $"even {n.Value}"), m => ((Number)m).Value % 2 == 0 ? null : "takes even numbers"),
            new VerbInflection("Odd", () => Says<Number>(n => $"odd {n.Value}"), m => ((Number)m).Value % 2 != 0 ? null : "takes odd numbers"),
        ]);
        builder.Chain(builder.AddSource(() => new Numbers(1, 2, 3)), pick, builder.Add(() => after));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(Wait.Join(after.Received.Select(w => w.Value))).IsEqualTo("odd 1,even 2,odd 3");
    }

    [Test]
    public async Task A_message_every_inflection_refuses_becomes_an_error_saying_why()
    {
        var unhandled = new ConcurrentQueue<ErrorMessage>();
        var builder = new PipelineBuilder(options: new PipelineOptions { UnhandledError = unhandled.Enqueue });
        var after = new Collect<Word>();
        var pick = builder.AddVerb("Pick",
        [
            new VerbInflection("Even", () => Says<Number>(n => $"even {n.Value}"), m => ((Number)m).Value % 2 == 0 ? null : "takes even numbers"),
            new VerbInflection("Positive", () => Says<Number>(n => $"positive {n.Value}"), m => ((Number)m).Value > 0 ? null : "takes positive numbers"),
        ]);
        builder.Chain(builder.AddSource(() => new Numbers(-1, 4)), pick, builder.Add(() => after));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(Wait.Join(after.Received.Select(w => w.Value))).IsEqualTo("even 4");
        await Assert.That(unhandled.Single().Error).IsEqualTo("None of Pick #1's inflections can take this message: Even takes even numbers; Positive takes positive numbers.");
        await Assert.That(((Number)unhandled.Single().Parent!).Value).IsEqualTo(-1);
        await Assert.That(pipeline.GetStats(pick).Refused).IsEqualTo(1);
    }

    [Test]
    public async Task An_inflection_that_throws_is_replaced_without_disturbing_the_others()
    {
        var builder = new PipelineBuilder(options: new PipelineOptions { UnhandledError = _ => { } });
        var after = new Collect<Word>();
        var created = new ConcurrentDictionary<string, int>();
        var describe = builder.AddVerb("Describe",
        [
            new VerbInflection("Words", () =>
            {
                created.AddOrUpdate("Words", 1, (_, n) => n + 1);
                return new Map<Word>((w, context) => throw new InvalidOperationException("boom"));
            }),
            new VerbInflection("Numbers", () =>
            {
                created.AddOrUpdate("Numbers", 1, (_, n) => n + 1);
                return Says<Number>(n => $"number {n.Value}");
            }),
        ]);
        builder.Chain(builder.AddSource(() => new Messages(new Word("x"), new Number(1))), describe, builder.Add(() => after));

        await using var pipeline = builder.Build();
        await Wait.For(pipeline.RunAsync());

        await Assert.That(Wait.Join(after.Received.Select(w => w.Value))).IsEqualTo("number 1");
        await Assert.That(created["Words"]).IsEqualTo(2);
        await Assert.That(created["Numbers"]).IsEqualTo(1);
    }

    [Test]
    public async Task The_closest_fit_is_a_messages_own_type_then_its_base_classes_then_interfaces_then_message()
    {
        await Assert.That(InflectionOrder.Rank(typeof(Number), typeof(Number))).IsEqualTo(0);
        await Assert.That(InflectionOrder.Rank(typeof(ErrorChild), typeof(ErrorMessage))).IsEqualTo(1);
        await Assert.That(InflectionOrder.Rank(typeof(ErrorChild), typeof(IHasText))).IsEqualTo(int.MaxValue - 1);
        await Assert.That(InflectionOrder.Rank(typeof(Number), typeof(Message))).IsEqualTo(int.MaxValue);
        await Assert.That(InflectionOrder.Rank(typeof(Number), typeof(Word))).IsNull();
    }

    private sealed record ErrorChild : ErrorMessage, IHasText
    {
        public string Text => Error;
    }
}
