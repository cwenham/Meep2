using Meep2.Cli;
using Meep2.SDK.Messages;
using Meep2.Tests.Support;

namespace Meep2.Tests.Meeplang;

/// <summary>Templates and #include (step 9).</summary>
public class TemplateTests
{
    private static string[] Lines(StringWriter output) =>
        [.. output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal)];

    [Test]
    public async Task A_template_is_copied_in_with_its_uses_values()
    {
        var run = await TestRun.Start("""
            const Base = "tick"
            template Label(Prefix, Times = 2)
                Where msg.Count <= Times
                    WriteLine "{Prefix} {Base} {msg.Count}"
            Interval 10ms count=3
                Label "a"
                Label "b" times=1
            """);

        await Assert.That(run.Diagnostics).IsEqualTo("");
        await Assert.That(Lines(run.Output)).IsEquivalentTo(["a tick 1", "a tick 2", "b tick 1"]);
    }

    [Test]
    public async Task What_is_under_a_use_goes_under_the_templates_last_module()
    {
        var run = await TestRun.Start("""
            template Early(Before = 3)
                Where msg.Count < Before
            Interval 10ms count=5
                Early as First
                    Sink
            """);

        await Assert.That(run.Diagnostics).IsEqualTo("");
        await Assert.That(Wait.Join(run.At("Sink").Cast<Meep2.Modules.Tick>().Select(t => t.Count))).IsEqualTo("1,2");
        await Assert.That(run.Result.Pipeline!.Modules.Select(m => m.ToString())).Contains("First"); // as names its first module
    }

    [Test]
    public async Task A_named_use_puts_its_name_before_the_names_inside_so_a_template_can_be_used_again()
    {
        var run = await TestRun.Start("""
            template Gated(Text)
                Where msg.Count > 0
                    Batch 1 as Gate
                        Batch 1
                            WriteLine "{Text} {fam.Gate.Count}"
            Interval 10ms count=1
                Gated "a" as A
                Gated "b" as B
            """);
        var unnamed = await TestRun.Start("""
            template Gated(Text)
                Where msg.Count < 5 as Gate
                    WriteLine "{Text}"
            Interval 10ms count=1
                Gated "a"
                Gated "b"
            """, run: false);

        await Assert.That(run.Diagnostics).IsEqualTo("");
        await Assert.That(Lines(run.Output)).IsEquivalentTo(["a 1", "b 1"]);
        await Assert.That(Wait.Join(run.Result.Pipeline!.Modules.Select(m => m.ToString()))).IsEqualTo("Interval #1,A,A_Gate,Batch #4,WriteLine #5,B,B_Gate,Batch #8,WriteLine #9");
        await Assert.That(unnamed.Diagnostics).Contains("There's already a module called Gate. It's named inside the template Gated: give each use of it a name, as in Gated as First, and the names inside start with that name (First_Gate).");
    }

    [Test]
    public async Task Templates_can_use_templates_and_values_can_use_msg()
    {
        var run = await TestRun.Start("""
            template Show(Text)
                WriteLine "{Text}"
            template Twice(Text)
                Show Text
                Show "again: {Text}"
            Interval 10ms count=1
                Twice "{msg.Count}"
            """);

        await Assert.That(run.Diagnostics).IsEqualTo("");
        await Assert.That(Lines(run.Output)).IsEquivalentTo(["1", "again: 1"]);
    }

    [Test]
    [Arguments("template Loop\n    Loop\nInterval 1s\n    Loop", "Loop uses itself, through Loop → Loop")]
    [Arguments("template Show(Text)\n    WriteLine Text\nInterval 1s\n    Show tetx=\"x\"", "The template Show has no parameter called tetx. Did you mean 'Text'?")]
    [Arguments("template Show(Text)\n    WriteLine Text\nInterval 1s\n    Show", "The template Show needs a value for Text.")]
    [Arguments("template Interval\n    WriteLine \"x\"\nInterval 1s\n    WriteLine \"y\"", "Interval is a module's verb, so a template can't be called that too.")]
    [Arguments("template Show\n    WriteLine \"x\"\nInterval 1s\n    Show @bypass", "A use of the template Show doesn't take @ modifiers.")]
    [Arguments("template Both\n    WriteLine \"x\"\n    WriteLine \"y\"\nInterval 1s\n    Both\n        Sink", "Both ends in 2 modules, so nothing can go under its use")]
    [Arguments("template Empty\nInterval 1s\n    Empty", "The template Empty has no modules indented under it.")]
    [Arguments("template Show(Text, Size = 1, Other)\n    WriteLine Text", "Other needs a default, since the parameter before it has one.")]
    [Arguments("  template Show\n", "This line is indented, but the line above can't have anything under it.")]
    public async Task Template_mistakes_say_what_they_are(string text, string expected)
    {
        var run = await TestRun.Start(text, run: false);

        await Assert.That(run.Diagnostics).Contains(expected);
    }

    [Test]
    public async Task Include_brings_in_another_files_templates_and_constants()
    {
        using var folder = new TemporaryFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, "shared"));
        folder.Write(Path.Combine("shared", "labels.meep"), """
            // Shared labels.
            #include "more.meep"
            const Base = "tick"
            template Label(Prefix)
                Shout "{Prefix} {Base} {msg.Count}"
            """);
        folder.Write(Path.Combine("shared", "more.meep"), """
            template Shout(Text)
                WriteLine "{Text}!"
            """);
        var file = folder.Write("main.meep", """
            #include "shared/labels.meep"
            Interval 10ms count=2
                Label "a"
            """);
        var output = new SharedText();
        var error = new SharedText();

        var code = await Wait.For(MeepCommand.RunAsync(["run", file], output, error, folder.Path, CancellationToken.None));

        await Assert.That(error.ToString()).IsEqualTo("");
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(Lines(output)).IsEquivalentTo(["a tick 1!", "a tick 2!"]);
    }

    [Test]
    public async Task Mistakes_in_an_included_file_point_at_that_file()
    {
        using var folder = new TemporaryFolder();
        var shared = folder.Write("shared.meep", """
            template Show(Text)
                WriteLin Text
            """);
        var file = folder.Write("main.meep", """
            #include "shared.meep"
            #include "missing.meep"
            Interval 10ms count=1
                Show "x"
            """);
        var loop = folder.Write("loop.meep", "#include \"loop2.meep\"\nInterval 1s\n");
        folder.Write("loop2.meep", "#include \"loop.meep\"\n");
        var modules = folder.Write("modules.meep", "#include \"bad.meep\"\nInterval 1s\n");
        folder.Write("bad.meep", "Interval 1s\n");
        var error = new SharedText();

        await MeepCommand.RunAsync(["run", file], new SharedText(), error, folder.Path, CancellationToken.None);
        var looped = new SharedText();
        await MeepCommand.RunAsync(["run", loop], new SharedText(), looped, folder.Path, CancellationToken.None);
        var holding = new SharedText();
        await MeepCommand.RunAsync(["run", modules], new SharedText(), holding, folder.Path, CancellationToken.None);

        await Assert.That(error.ToString()).Contains($"{file}(2,1): error: There's no file {Path.Combine(folder.Path, "missing.meep")} to include.");
        await Assert.That(looped.ToString()).Contains("loop.meep is already being included, so including it here would go round in a loop.");
        await Assert.That(holding.ToString()).Contains($"{Path.Combine(folder.Path, "bad.meep")}(1,1): error: An included file holds templates and constants, not a pipeline's modules.");

        // With the missing include gone, the mistake in the template is reported where it's written.
        File.WriteAllText(file, "#include \"shared.meep\"\nInterval 10ms count=1\n    Show \"x\"\n");
        var inTemplate = new SharedText();
        await MeepCommand.RunAsync(["run", file], new SharedText(), inTemplate, folder.Path, CancellationToken.None);
        await Assert.That(inTemplate.ToString()).Contains($"{shared}(2,5): error: There's no module called WriteLin. Did you mean 'WriteLine'?");
    }

    [Test]
    public async Task A_running_pipeline_follows_a_change_to_a_uses_values()
    {
        await using var run = TestRun.BeginLive("""
            template Counted(Step)
                Where msg.Count > 0
                    Tally step=Step
                        Sink
            Interval 5ms
                Counted 1
            """);
        await Wait.Until(() => run.Numbers.Count >= 3);

        var change = await run.Pipeline.ChangeAsync("""
            template Counted(Step)
                Where msg.Count > 0
                    Tally step=Step
                        Sink
            Interval 5ms
                Counted 1000
            """);
        await Wait.Until(() => run.Numbers.Any(n => n >= 1000));

        await Assert.That(change.Applied).IsTrue();
        await Assert.That(Wait.Join(change.Report!.Replaced.Select(m => m.ToString()))).IsEqualTo("Tally #3");
        await Assert.That(Wait.Join(change.Report.Unchanged.Select(m => m.ToString()))).IsEqualTo("Interval #1,Where #2,Sink #4");
    }

    [Test]
    public async Task Watching_follows_the_files_a_pipeline_includes()
    {
        using var folder = new TemporaryFolder();
        var shared = folder.Write("shared.meep", "template Say\n    WriteLine \"tick {msg.Count}\"\n");
        var file = folder.Write("main.meep", "#include \"shared.meep\"\nInterval 20ms\n    Say\n");
        var output = new SharedText();
        using var stop = new CancellationTokenSource();

        var run = MeepCommand.RunAsync(["run", "--watch", file], TextWriter.Synchronized(output), TextWriter.Synchronized(new SharedText()), folder.Path, stop.Token);
        await Wait.Until(() => output.ToString().Contains("tick ", StringComparison.Ordinal));
        var deadline = DateTime.UtcNow + Wait.Limit;
        while (!output.ToString().Contains("tock ", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
        {
            File.WriteAllText(shared, "template Say\n    WriteLine \"tock {msg.Count}\"\n");
            await Task.Delay(1000);
        }
        await stop.CancelAsync();
        await Wait.For(run);

        await Assert.That(output.ToString()).Contains("tock ");
    }
}
