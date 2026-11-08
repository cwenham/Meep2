using Meep2.Modules;
using Meep2.SDK.Messages;
using Meep2.Tests.Support;

namespace Meep2.Tests.Meeplang;

public class LoaderTests
{
    private static string Counts(IEnumerable<Message> messages) => Wait.Join(messages.Cast<Tick>().Select(t => t.Count));

    [Test]
    public async Task A_staircase_pipeline_runs()
    {
        var run = await TestRun.Start("""
            Interval 10ms count=3
                Sink
            """);

        await Assert.That(run.Diagnostics).IsEqualTo("");
        await Assert.That(Counts(run.At("Sink"))).IsEqualTo("1,2,3");
    }

    [Test]
    public async Task Modules_at_the_same_indentation_are_branches()
    {
        var run = await TestRun.Start("""
            Interval 10ms count=2
                Sink as Left
                Sink as Right
            """);

        await Assert.That(Counts(run.At("Left"))).IsEqualTo("1,2");
        await Assert.That(Counts(run.At("Right"))).IsEqualTo("1,2");
    }

    [Test]
    public async Task Where_filters_with_a_typed_condition()
    {
        var run = await TestRun.Start("""
            const Minimum = 2
            Interval 10ms count=4
                Where msg.Count >= Minimum
                    Sink
            """);

        await Assert.That(Counts(run.At("Sink"))).IsEqualTo("2,3,4");
    }

    [Test]
    public async Task Plus_lines_merge_several_sources_into_one_module()
    {
        var run = await TestRun.Start("""
            + Interval 10ms count=2 payload="a"
            + Interval 10ms count=3 payload="b"
            Sink
            """);

        var payloads = run.At("Sink").Cast<Tick>().Select(t => t.Payload).Order();
        await Assert.That(Wait.Join(payloads)).IsEqualTo("a,a,b,b,b");
    }

    [Test]
    public async Task Every_end_of_a_plus_chain_feeds_the_next_line()
    {
        var run = await TestRun.Start("""
            + Interval 10ms count=3
                  Where msg.Count == 1
                  Where msg.Count == 3
            Sink
            """);

        await Assert.That(Wait.Join(run.At("Sink").Cast<Tick>().Select(t => t.Count).Order())).IsEqualTo("1,3");
    }

    [Test]
    public async Task Tap_receives_what_a_named_module_emits()
    {
        var run = await TestRun.Start("""
            Interval 10ms count=2 as Clock

            Tap Clock
                Sink
            """);

        await Assert.That(Counts(run.At("Sink"))).IsEqualTo("1,2");
    }

    [Test]
    public async Task Tap_with_errors_receives_only_errors_and_they_are_handled()
    {
        var run = await TestRun.Start("""
            Interval 10ms count=2
                Boom as Broken
                    Sink as Normal

            Tap Broken @errors
                Sink as Errors
            """);

        await Assert.That(run.At("Errors").Count).IsEqualTo(2);
        await Assert.That(run.At("Errors").All(m => m is ErrorMessage { Error: "boom" })).IsTrue();
        await Assert.That(run.At("Normal").Count).IsEqualTo(2); // errors also go down Boom's own branch
        await Assert.That(run.Unhandled.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Errors_skip_a_module_whose_expressions_dont_fit_them()
    {
        var run = await TestRun.Start("""
            Interval 10ms count=2
                Boom
                    Where msg.Count > 0
                        Sink
            """);

        await Assert.That(run.At("Sink").Count).IsEqualTo(2);
        await Assert.That(run.At("Sink").All(m => m is ErrorMessage)).IsTrue();
    }

    [Test]
    public async Task Templates_use_constants_and_message_members()
    {
        var run = await TestRun.Start("""
            const Greeting = "tick"
            Interval 10ms count=2
                WriteLine "{Greeting} {msg.Count:00}"
            """);

        await Assert.That(run.Output.ToString().ReplaceLineEndings("\n")).IsEqualTo("tick 01\ntick 02\n");
    }

    [Test]
    public async Task A_source_registered_by_its_verb_attribute_takes_its_parameters()
    {
        var run = await TestRun.Start("""
            Numbers 3
                Where msg.Value != 2
                    Sink
            """);

        await Assert.That(Wait.Join(run.At("Sink").Cast<Number>().Select(n => n.Value))).IsEqualTo("1,3");
    }

    [Test]
    public async Task Defaults_set_the_pipelines_buffer_size()
    {
        var run = await TestRun.Start("""
            defaults
                buffer = 50
            Interval 10ms count=1
                Sink
            """, run: false);

        await Assert.That(run.Result.Pipeline!.Options.BufferSize).IsEqualTo(50);
    }

    [Test]
    [Arguments("Intervall 1s", "(1,1): error: There's no module called Intervall. Did you mean 'Interval'?")]
    [Arguments("Interval 1s cuont=3", "(1,13): error: Interval has no parameter called cuont. Did you mean 'count'?")]
    [Arguments("Interval", "(1,1): error: Interval needs a value after its name, for its period.")]
    [Arguments("Interval 0s", "(1,10): error: period: The period must be longer than zero.")]
    [Arguments("Interval 1s count=\"x\"", "(1,19): error: Expected a whole number, but this is the text \"x\".")]
    [Arguments("Interval 1s count=2.5", "(1,19): error: Expected a whole number, but this is the number 2.5.")]
    [Arguments("Interval 1s count=10000000000000000000", "(1,19): error: Expected a whole number from -9,223,372,036,854,775,808 to 9,223,372,036,854,775,807, but this is the number 10000000000000000000.")]
    [Arguments("Interval 1s @bypass", "(1,13): error: Interval is a source, so @bypass doesn't apply to it.")]
    [Arguments("Interval 1s\n    Where msg.Count > 1 @expires=5s", "(2,25): error: @expires arrives with expiry and the error policy, in step 4.")]
    [Arguments("Where msg.Count > 1", "(1,1): error: Nothing feeds Where. Indent it under a source, or feed it with + lines above it.")]
    [Arguments("+ Where msg.Count > 1\nSink", "(1,3): error: A + line starts a chain of its own, so it needs a source or a Tap, not Where.")]
    [Arguments("Interval 1s\n    Interval 2s", "(2,5): error: Interval is a source, so it can't go under another module.")]
    [Arguments("Tap Clock\n    Sink", "(1,5): error: There's no module called Clock.")]
    [Arguments("Interval 1s\n    Sink\nTap Interval\n    Sink as Out", "(3,5): error: There's no module called Interval. Modules are found by name, so give the Interval you mean a name with 'as', and use that.")]
    [Arguments("Interval 1s as Clock\nTap Clok\n    Sink", "(2,5): error: There's no module called Clok. Did you mean 'Clock'?")]
    [Arguments("Interval 1s\n    Extract \"$.x\"", "(2,5): error: Extract takes IHasText, but only Tick can reach it, so it would never receive anything.")]
    [Arguments("Interval 1s\n    Where msg.Cuont > 1", "(2,15): error: Tick has no 'Cuont'. Did you mean 'Count'?")]
    [Arguments("Interval 1s as A\nInterval 2s as A", "(2,16): error: There's already a module called A.")]
    [Arguments("const Bad = msg.Count\nInterval 1s", "(1,13): error: msg is only there when a message arrives, so it can't be used here.")]
    [Arguments("Interval 1s\n    Extract \"x.y\"", "(2,13): error: A JSONPath query starts with $ in the JSONPath query x.y (at character 1).")]
    [Arguments("const Key = secret(\"api-key\")\nInterval 1s", "(1,13): error: secret() isn't supported yet. It arrives with the secret store.")]
    [Arguments("Interval 1s\n    Fetch.HTTPGet \"https://example.com\"", "(2,5): error: Choosing an inflection with Fetch.HTTPGet arrives with verbs, in step 4.")]
    public async Task Mistakes_are_reported_with_their_position(string text, string expected)
    {
        var run = await TestRun.Start(text, run: false);

        await Assert.That(run.Result.Succeeded).IsFalse();
        await Assert.That(run.Diagnostics).Contains(expected);
    }

    [Test]
    public async Task A_number_with_too_many_digits_is_reported_where_it_is_written()
    {
        var run = await TestRun.Start("const Huge = 1" + new string('0', 1000) + "\nInterval 1s", run: false);

        await Assert.That(run.Diagnostics).Contains("(1,14): error: This number has more than 1,000 digits, which is more than Meeplang works with.");
    }

    [Test]
    public async Task An_expression_after_a_module_that_doesnt_declare_its_output_gets_a_warning()
    {
        var run = await TestRun.Start("""
            Interval 10ms count=1
                Sink as First
                    Where msg.Anything == 1
            """, run: false);

        await Assert.That(run.Result.Succeeded).IsTrue();
        await Assert.That(run.Diagnostics).Contains("warning: Meeplang can't check msg.Anything == 1 before the pipeline runs");
    }
}
