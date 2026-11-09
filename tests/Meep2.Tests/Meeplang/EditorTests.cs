using Meep2.Meeplang.Editing;
using Meep2.Tests.Support;

namespace Meep2.Tests.Meeplang;

/// <summary>Editing a pipeline file's text while keeping its comments and layout (goal 4, step 8).</summary>
public class EditorTests
{
    private const string Feed = """
        // Departures from the stop outside.
        Interval 30s count=100            // every half minute
            Fetch "https://example.com/departures.json" as Departures @buffer=10
                Extract "$.departures[*]"
                    // Only buses that are close.
                    Where msg.minutesAway < 30
                        WriteLine "{msg.route} in {msg.minutesAway} min"
        """;

    private static string Edit(string text, params SourceEdit[] edits) =>
        SourceEditor.Apply(Script.Parse(text), edits).Source.Text;

    [Test]
    public async Task Setting_a_parameter_changes_only_its_value()
    {
        var text = Edit(Feed, new SourceEdit("Interval", EditKind.Set, "count", "5"));

        // Its comment stays in its column.
        await Assert.That(text).IsEqualTo(Feed.Replace("count=100 ", "count=5   ", StringComparison.Ordinal));
    }

    [Test]
    public async Task A_comment_later_on_the_line_keeps_its_column()
    {
        const string Aligned = "Interval 500ms              // twice a second\n    WriteLine \"{msg}\"  // \"quoted\" // twice\n";

        var longer = Edit(Aligned, new SourceEdit("Interval", EditKind.Set, "count", "1000"));
        var shorter = Edit(Aligned, new SourceEdit("Interval", EditKind.Set, null, "1s"));
        var crowded = Edit(Aligned, new SourceEdit("WriteLine", EditKind.Set, null, "\"{msg.Count} {msg.Created}\""));

        await Assert.That(longer).StartsWith("Interval 500ms count=1000   // twice a second\n");
        await Assert.That(shorter).StartsWith("Interval 1s" + new string(' ', 17) + "// twice a second\n");
        await Assert.That(crowded).Contains("WriteLine \"{msg.Count} {msg.Created}\" // \"quoted\" // twice");
    }

    [Test]
    public async Task A_new_parameter_goes_after_the_others_and_before_the_name()
    {
        var text = Edit(Feed, new SourceEdit("Departures", EditKind.Set, "timeout", "5s"));

        await Assert.That(text).Contains("""Fetch "https://example.com/departures.json" timeout=5s as Departures @buffer=10""");
    }

    [Test]
    public async Task The_main_value_can_be_set_or_removed()
    {
        var changed = Edit(Feed, new SourceEdit("Interval", EditKind.Set, null, "1m"));
        var removed = Edit("Fetch \"x\" timeout=5s\n", new SourceEdit("Fetch", EditKind.Unset));

        await Assert.That(changed).Contains("Interval 1m count=100" + new string(' ', 13) + "// every half minute");
        await Assert.That(removed).IsEqualTo("Fetch timeout=5s\n");
    }

    [Test]
    public async Task Parameters_on_lines_of_their_own_stay_that_way()
    {
        const string Text = """
            Fetch "https://example.com"
                timeout = 5s   // generous
                retries = 2
                WriteLine "{msg}"
            """;

        var added = Edit(Text, new SourceEdit("Fetch", EditKind.Set, "method", "\"POST\""));
        var changed = Edit(Text, new SourceEdit("Fetch", EditKind.Set, "timeout", "10s"));
        var removed = Edit(Text, new SourceEdit("Fetch", EditKind.Unset, "retries"));

        await Assert.That(added).IsEqualTo("""
            Fetch "https://example.com"
                timeout = 5s   // generous
                retries = 2
                method = "POST"
                WriteLine "{msg}"
            """);
        await Assert.That(changed).Contains("    timeout = 10s  // generous");
        await Assert.That(removed).DoesNotContain("retries");
        await Assert.That(removed).Contains("    WriteLine");
    }

    [Test]
    public async Task Modifiers_are_set_changed_and_removed()
    {
        var added = Edit(Feed, new SourceEdit("Extract", EditKind.SetModifier, "bypass"));
        var changed = Edit(Feed, new SourceEdit("Departures", EditKind.SetModifier, "buffer", "50"));
        var policy = Edit(Feed, new SourceEdit("Extract", EditKind.SetModifier, "on-error", "stop-branch after 5 in 1s"));
        var removed = Edit(Feed, new SourceEdit("Departures", EditKind.RemoveModifier, "buffer"));

        await Assert.That(added).Contains("""        Extract "$.departures[*]" @bypass""");
        await Assert.That(changed).Contains("as Departures @buffer=50");
        await Assert.That(policy).Contains("""@on-error="stop-branch after 5 in 1s" """.TrimEnd());
        await Assert.That(removed).Contains("""Fetch "https://example.com/departures.json" as Departures""" + "\n");
    }

    [Test]
    public async Task Names_are_given_changed_and_taken_away()
    {
        var given = Edit(Feed, new SourceEdit("Where", EditKind.Rename, Value: "Close"));
        var changed = Edit(Feed, new SourceEdit("Departures", EditKind.Rename, Value: "Board"));
        var taken = Edit(Feed, new SourceEdit("Departures", EditKind.Rename));

        await Assert.That(given).Contains("Where msg.minutesAway < 30 as Close");
        await Assert.That(changed).Contains("as Board @buffer=10");
        await Assert.That(taken).Contains("""Fetch "https://example.com/departures.json" @buffer=10""");
        await Assert.That(() => Edit(Feed, new SourceEdit("Where", EditKind.Rename, Value: "not a name"))).Throws<EditException>();
    }

    [Test]
    public async Task Adding_a_branch_puts_it_after_the_others_at_their_indentation()
    {
        var text = Edit(Feed, new SourceEdit("Departures", EditKind.Add, Value: "WriteLine \"{msg}\""));

        await Assert.That(text).IsEqualTo(Feed + "\n        WriteLine \"{msg}\"");
    }

    [Test]
    public async Task Inserting_mid_chain_moves_what_was_under_the_module_in_a_level()
    {
        var text = Edit(Feed, new SourceEdit("Extract", EditKind.Insert, Value: "Where msg.route != \"X\""));

        await Assert.That(text).IsEqualTo("""
            // Departures from the stop outside.
            Interval 30s count=100            // every half minute
                Fetch "https://example.com/departures.json" as Departures @buffer=10
                    Extract "$.departures[*]"
                        Where msg.route != "X"
                            // Only buses that are close.
                            Where msg.minutesAway < 30
                                WriteLine "{msg.route} in {msg.minutesAway} min"
            """);
    }

    [Test]
    public async Task Removing_a_module_moves_what_was_under_it_out_a_level()
    {
        var text = Edit(Feed, new SourceEdit("Extract", EditKind.Remove));
        var branch = Edit(Feed, new SourceEdit("Extract", EditKind.RemoveBranch));

        await Assert.That(text).IsEqualTo("""
            // Departures from the stop outside.
            Interval 30s count=100            // every half minute
                Fetch "https://example.com/departures.json" as Departures @buffer=10
                    // Only buses that are close.
                    Where msg.minutesAway < 30
                        WriteLine "{msg.route} in {msg.minutesAway} min"
            """);
        await Assert.That(branch).IsEqualTo("""
            // Departures from the stop outside.
            Interval 30s count=100            // every half minute
                Fetch "https://example.com/departures.json" as Departures @buffer=10
            """);
    }

    [Test]
    public async Task New_lines_follow_the_files_own_indentation()
    {
        const string TwoSpaces = "Interval 1s\n  WriteLine \"{msg}\"\n";

        var text = Edit(TwoSpaces, new SourceEdit("WriteLine", EditKind.Add, Value: "Sink"));
        var crlf = Edit("Interval 1s\r\n    WriteLine \"{msg}\"\r\n", new SourceEdit("Interval", EditKind.Set, "count", "3"));

        await Assert.That(text).IsEqualTo("Interval 1s\n  WriteLine \"{msg}\"\n    Sink\n");
        await Assert.That(crlf).IsEqualTo("Interval 1s count=3\r\n    WriteLine \"{msg}\"\r\n");
    }

    [Test]
    public async Task Several_edits_each_see_the_last_ones_result()
    {
        var text = Edit(Feed,
            new SourceEdit("Extract", EditKind.Rename, Value: "Each"),
            new SourceEdit("Each", EditKind.Set, "limit", "10"),
            new SourceEdit("5", EditKind.Set, null, "\"{msg.route}\""));

        await Assert.That(text).Contains("""Extract "$.departures[*]" limit=10 as Each""");
        await Assert.That(text).Contains("""WriteLine "{msg.route}" """.TrimEnd());
    }

    [Test]
    public async Task Defaults_are_set_added_and_removed()
    {
        const string WithDefaults = """
            // Ticks.
            defaults
                buffer = 10      // small
            Interval 1s
                WriteLine "{msg}"
            """;

        var changed = Edit(WithDefaults, new SourceEdit("", EditKind.SetDefault, "buffer", "20"));
        var added = Edit(WithDefaults, new SourceEdit("", EditKind.SetDefault, "expires", "5s"));
        var removed = Edit(WithDefaults, new SourceEdit("", EditKind.UnsetDefault, "buffer"));
        var started = Edit(Feed, new SourceEdit("", EditKind.SetDefault, "on-error", "log, stop-branch after 5 in 1s"));

        await Assert.That(changed).Contains("    buffer = 20      // small");
        await Assert.That(added).Contains("    buffer = 10      // small\n    expires = 5s\nInterval 1s");
        await Assert.That(removed).IsEqualTo("// Ticks.\nInterval 1s\n    WriteLine \"{msg}\"");
        // A comment above the first module stays with it, so a new block goes above that.
        await Assert.That(started).StartsWith("defaults\n    on-error = log, stop-branch after 5 in 1s\n\n// Departures from the stop outside.\nInterval");
        await Assert.That(() => Edit(Feed, new SourceEdit("", EditKind.UnsetDefault, "buffer"))).Throws<EditException>().WithMessageContaining("The file has no defaults block.");
    }

    [Test]
    public async Task Constants_are_declared_changed_and_removed()
    {
        const string WithHeader = "// A greeting.\n\nconst Greeting = \"hi\"\nInterval 1s\n    WriteLine \"{Greeting}\"\n";

        var changed = Edit(WithHeader, new SourceEdit("", EditKind.SetConstant, "Greeting", "\"hello\""));
        var added = Edit(WithHeader, new SourceEdit("", EditKind.SetConstant, "Name", "\"Ann\""));
        var removed = Edit(WithHeader, new SourceEdit("", EditKind.UnsetConstant, "Greeting"));
        var first = Edit("// A header.\n\nInterval 1s\n", new SourceEdit("", EditKind.SetConstant, "Period", "1s"));

        await Assert.That(changed).Contains("const Greeting = \"hello\"\n");
        await Assert.That(added).Contains("const Greeting = \"hi\"\nconst Name = \"Ann\"\nInterval");
        await Assert.That(removed).IsEqualTo("// A greeting.\n\nInterval 1s\n    WriteLine \"{Greeting}\"\n");
        await Assert.That(first).IsEqualTo("// A header.\n\nconst Period = 1s\n\nInterval 1s\n");
        await Assert.That(() => Edit(WithHeader, new SourceEdit("", EditKind.UnsetConstant, "Greting"))).Throws<EditException>().WithMessageContaining("Did you mean 'Greeting'?");
    }

    [Test]
    public async Task Plugins_are_added_after_the_settings_and_removed_by_any_of_their_names()
    {
        const string Text = "const Stop = \"42\"\n#plugin \"../Transit/bin/Transit.dll\" as Buses\nInterval 1m\n";

        var added = Edit(Text, new SourceEdit("", EditKind.AddPlugin, null, "nuget:Meep.Weather 1.2.0"));
        var first = Edit("Interval 1m\n", new SourceEdit("", EditKind.AddPlugin, null, "#plugin nuget:Meep.Weather 1.2.0"));
        var byAlias = Edit(Text, new SourceEdit("", EditKind.RemovePlugin, "Buses"));
        var byFile = Edit(Text, new SourceEdit("", EditKind.RemovePlugin, "Transit"));

        await Assert.That(added).IsEqualTo("const Stop = \"42\"\n#plugin \"../Transit/bin/Transit.dll\" as Buses\n#plugin nuget:Meep.Weather 1.2.0\nInterval 1m\n");
        await Assert.That(first).IsEqualTo("#plugin nuget:Meep.Weather 1.2.0\n\nInterval 1m\n");
        await Assert.That(byAlias).IsEqualTo("const Stop = \"42\"\nInterval 1m\n");
        await Assert.That(byFile).IsEqualTo(byAlias);
        await Assert.That(() => Edit(Text, new SourceEdit("", EditKind.RemovePlugin, "Weather"))).Throws<EditException>().WithMessageContaining("There's no plugin called Weather.");
    }

    [Test]
    public async Task An_edit_says_which_module_it_couldnt_find()
    {
        const string Two = "Interval 1s\n    WriteLine \"a\"\n    WriteLine \"b\"\n";

        await Assert.That(() => Edit(Two, new SourceEdit("WriteLine", EditKind.Unset))).Throws<EditException>().WithMessageContaining("There's more than one WriteLine: WriteLine #2, WriteLine #3. Say which by number.");
        await Assert.That(() => Edit(Two, new SourceEdit("Fetch", EditKind.Unset))).Throws<EditException>().WithMessageContaining("There's no module called Fetch.");
        await Assert.That(() => Edit(Two, new SourceEdit("9", EditKind.Unset))).Throws<EditException>().WithMessageContaining("There's no module 9: the file has 3.");
        await Assert.That(() => Edit(Two, new SourceEdit("2", EditKind.Unset, "nothing"))).Throws<EditException>().WithMessageContaining("WriteLine has no parameter called nothing.");
        await Assert.That(Edit(Two, new SourceEdit("WriteLine #3", EditKind.Set, null, "\"c\""))).Contains("WriteLine \"c\""); // as the messages name it
    }
}
