using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Tests.Support;

namespace Meep2.Tests.Meeplang;

public class ParserTests
{
    [Test]
    public async Task Indentation_puts_downstream_modules_under_their_upstream()
    {
        var syntax = Script.Parse("""
            Interval 1m
                Fetch "https://example.com" as Schedule
                    timeout = 5s
                    Extract "$.departures[*]"
                        Where msg.minutesAway < 30
                    Save "~/bus.jsonl"

            Tap Schedule @errors
                WriteLine "failed: {msg.Error}"
            """);

        await Assert.That(Script.Diagnostics(syntax)).IsEqualTo("");
        await Assert.That(syntax.Modules.Count).IsEqualTo(2);
        var fetch = syntax.Modules[0].Children.Single();
        await Assert.That(fetch.Verb).IsEqualTo("Fetch");
        await Assert.That(fetch.Name).IsEqualTo("Schedule");
        await Assert.That(fetch.Arguments.Single().Name).IsEqualTo("timeout");
        await Assert.That(fetch.Arguments.Single().Value).IsEqualTo(new LiteralSyntax(TimeSpan.FromSeconds(5), fetch.Arguments.Single().Value.Span));
        await Assert.That(string.Join(",", fetch.Children.Select(c => c.Verb))).IsEqualTo("Extract,Save");
        await Assert.That(fetch.Children[0].Children.Single().Verb).IsEqualTo("Where");
        var tap = syntax.Modules[1];
        await Assert.That(tap.Modifiers.Single().Name).IsEqualTo("errors");
        await Assert.That(tap.Children.Single().Verb).IsEqualTo("WriteLine");
    }

    [Test]
    public async Task A_module_line_holds_a_value_named_parameters_a_name_and_modifiers()
    {
        var module = Script.Parse("""
            Interval 500ms count=3 payload="x" as Clock @buffer=10 @no-errors
            """).Modules.Single();

        await Assert.That(module.Positional).IsTypeOf<LiteralSyntax>();
        await Assert.That(((LiteralSyntax)module.Positional!).Value).IsEqualTo(TimeSpan.FromMilliseconds(500));
        await Assert.That(string.Join(",", module.Arguments.Select(a => a.Name))).IsEqualTo("count,payload");
        await Assert.That(module.Name).IsEqualTo("Clock");
        await Assert.That(string.Join(",", module.Modifiers.Select(m => m.Name))).IsEqualTo("buffer,no-errors");
        await Assert.That(((LiteralSyntax)module.Modifiers[0].Value!).Value).IsEqualTo(10m);
    }

    [Test]
    public async Task Kebab_case_names_are_read_where_a_name_is_expected()
    {
        var module = Script.Parse("""
            Fetch "x" max-size=10
                on-error = 3
            """).Modules.Single();

        await Assert.That(string.Join(",", module.Arguments.Select(a => a.Name))).IsEqualTo("max-size,on-error");
    }

    [Test]
    public async Task Plus_lines_are_marked_as_feeders()
    {
        var syntax = Script.Parse("""
            + Interval 1s
            + Interval 2s
            WriteLine
            """);

        await Assert.That(Script.Diagnostics(syntax)).IsEqualTo("");
        await Assert.That(string.Join(",", syntax.Modules.Select(m => m.IsFeeder))).IsEqualTo("True,True,False");
    }

    [Test]
    public async Task A_plus_line_with_nothing_after_it_is_an_error()
    {
        var syntax = Script.Parse("""
            Interval 1s
            + Interval 2s
            """);

        await Assert.That(Script.Diagnostics(syntax)).Contains("test.meep(2,3): error: Nothing follows this + line");
    }

    [Test]
    public async Task Comments_are_ignored_but_slashes_inside_strings_are_kept()
    {
        var syntax = Script.Parse("""
            // A whole-line comment
            Fetch "https://example.com" // a trailing comment
            """);

        await Assert.That(Script.Diagnostics(syntax)).IsEqualTo("");
        await Assert.That(((LiteralSyntax)syntax.Modules.Single().Positional!).Value).IsEqualTo("https://example.com");
    }

    [Test]
    public async Task Indentation_that_lines_up_with_nothing_is_an_error()
    {
        var syntax = Script.Parse("""
            Interval 1s
                    Fetch "x"
                WriteLine
            """);

        await Assert.That(Script.Diagnostics(syntax)).Contains("(3,5): error: This line's indentation doesn't line up");
    }

    [Test]
    public async Task Constants_and_defaults_come_before_modules()
    {
        var syntax = Script.Parse("""
            defaults
                buffer = 500
            const Api = "https://example.com"
            Interval 1s
            const Late = 1
            """);

        await Assert.That(syntax.Defaults.Single().Name).IsEqualTo("buffer");
        await Assert.That(syntax.Constants.Single().Name).IsEqualTo("Api");
        await Assert.That(Script.Diagnostics(syntax)).Contains("(5,1): error: Constants go before the pipeline's modules.");
    }

    [Test]
    public async Task Plugin_lines_name_github_nuget_and_local_plugins()
    {
        var syntax = Script.Parse($$"""
            const Stop = "Hove"
            #plugin github.com/cwenham/MeepTransit 1.2.0 sha256:{{new string('A', 64)}}
            #plugin nuget:Meep.Transit 2.0.0-beta.1   // a comment
            #plugin '../Transit/bin/Debug/net11.0/Transit.dll'
            Interval 1s
            """);

        await Assert.That(Script.Diagnostics(syntax)).IsEmpty();
        await Assert.That(syntax.Plugins.Count).IsEqualTo(3);
        await Assert.That(syntax.Plugins[0]).IsEqualTo(new PluginDirective(PluginOrigin.GitHub, "cwenham/MeepTransit", "1.2.0", new string('a', 64), new SourceSpan(2, 1, 116)));
        await Assert.That(syntax.Plugins[1].ToString()).IsEqualTo("nuget:Meep.Transit 2.0.0-beta.1");
        await Assert.That(syntax.Plugins[1].Sha256).IsNull();
        await Assert.That(syntax.Plugins[2].Origin).IsEqualTo(PluginOrigin.Local);
        await Assert.That(syntax.Plugins[2].Name).IsEqualTo("../Transit/bin/Debug/net11.0/Transit.dll");
    }

    [Test]
    public async Task Plugin_lines_report_their_mistakes()
    {
        var syntax = Script.Parse("""
            #plugin github.com/cwenham/MeepTransit 1.2.0
            #plugin github.com/cwenham 1.2.0 sha256:abc
            #plugin nuget:Meep.Transit
            #plugin nuget:Meep.Transit 1.0 sha256:xyz
            #plugin ftp://example.com/plugin.zip
            #plugin "local.dll" 1.0
            #plugins nuget:Meep.Transit 1.0
            Interval 1s
            #plugin nuget:Late 1.0
            """);
        var diagnostics = Script.Diagnostics(syntax);

        await Assert.That(diagnostics).Contains("(1,1): error: github.com/cwenham/MeepTransit 1.2.0 needs the SHA-256 of its release asset, as in sha256:3f9a…, so the release can't change under you.");
        await Assert.That(diagnostics).Contains("(2,9): error: A plugin on GitHub is named as github.com/owner/repo, not github.com/cwenham.");
        await Assert.That(diagnostics).Contains("(3,9): error: nuget:Meep.Transit needs a version, as in nuget:Meep.Transit 1.2.0.");
        await Assert.That(diagnostics).Contains("(4,32): error: A plugin's SHA is written as sha256: and 64 hex digits.");
        await Assert.That(diagnostics).Contains("(5,9): error: Write a plugin as #plugin github.com/owner/repo 1.2.0 sha256:…");
        await Assert.That(diagnostics).Contains("(6,21): error: A plugin on this machine takes just its path, with no version or SHA.");
        await Assert.That(diagnostics).Contains("(7,1): error: There's no #plugins directive. Meeplang has #plugin and #include.");
        await Assert.That(diagnostics).Contains("(9,1): error: #plugin goes before the pipeline's modules.");
        await Assert.That(syntax.Plugins.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_plugin_line_can_name_the_plugin_and_modules_can_name_plugins()
    {
        var syntax = Script.Parse("""
            #plugin nuget:Meep.Transit 2.0.0 as Transit
            #plugin "local.dll" as Mine
            #plugin "other.dll" as 9lives
            #plugin "other.dll" as
            Transit.Departures.Live
                Mine.Shout
            """);
        var diagnostics = Script.Diagnostics(syntax);

        await Assert.That(diagnostics).Contains("(3,24): error: A plugin's name after 'as' is a word of letters and digits, such as Transit, not 9lives.");
        await Assert.That(diagnostics).Contains("(4,21): error: Expected a name for the plugin after 'as'.");
        await Assert.That(syntax.Plugins.Count).IsEqualTo(2);
        await Assert.That(syntax.Plugins[0].Alias).IsEqualTo("Transit");
        await Assert.That(syntax.Plugins[0].ToString()).IsEqualTo("nuget:Meep.Transit 2.0.0");
        await Assert.That(syntax.Plugins[1].Alias).IsEqualTo("Mine");
        var module = syntax.Modules[0];
        await Assert.That((module.Plugin, module.Verb, module.Inflection)).IsEqualTo(("Transit", "Departures", "Live"));
        var child = module.Children[0];
        await Assert.That((child.Plugin, child.Verb, child.Inflection)).IsEqualTo(((string?)null, "Mine", "Shout"));
    }

    [Test]
    public async Task By_gives_a_modules_key_as_a_keyword()
    {
        var syntax = Script.Parse("""
            Interval 1s
                Distribute 4 by msg.Count % 4 as Spread
                    Sink
                Zip by msg.Count
            """);
        var distribute = syntax.Modules[0].Children[0];
        var zip = syntax.Modules[0].Children[1];

        await Assert.That(Script.Diagnostics(syntax)).IsEqualTo("");
        await Assert.That((distribute.Arguments[0].Name, distribute.Arguments[0].Value is BinarySyntax, distribute.Name)).IsEqualTo(("by", true, "Spread"));
        await Assert.That((zip.Positional, zip.Arguments[0].Name)).IsEqualTo(((ExpressionSyntax?)null, "by"));
        await Assert.That(Script.Diagnostics(Script.Parse("Interval 1s\n    Zip by=msg.Count\n"))).Contains("Write by without =, as in by msg.Key.");
        await Assert.That(Script.Diagnostics(Script.Parse("Interval 1s\n    Zip\n        by = msg.Count\n"))).Contains("Write by on the module's line, without =");
    }

    [Test]
    [Arguments("5s", 5_000)]
    [Arguments("250ms", 250)]
    [Arguments("1.5m", 90_000)]
    [Arguments("1h30m", 5_400_000)]
    [Arguments("2d", 172_800_000)]
    [Arguments("00:00:05", 5_000)]
    [Arguments("1.00:00:00", 86_400_000)]
    public async Task Durations_can_be_written_with_units_or_as_TimeSpan(string text, long milliseconds)
    {
        var expression = MeeplangParser.ParseExpression(text, out var diagnostics);

        await Assert.That(diagnostics.Count).IsEqualTo(0);
        await Assert.That(((LiteralSyntax)expression!).Value).IsEqualTo(TimeSpan.FromMilliseconds(milliseconds));
    }

    [Test]
    public async Task An_unknown_duration_unit_is_an_error()
    {
        MeeplangParser.ParseExpression("5min", out var diagnostics);

        await Assert.That(diagnostics.Single().Message).Contains("'min' isn't a unit");
    }

    [Test]
    public async Task Templates_split_into_text_and_holes_with_formats()
    {
        var expression = (TemplateSyntax)MeeplangParser.ParseExpression("\"{{x}} {msg.Created:yyyy-MM-dd} at \\\"{Api}\\\"\"", out _)!;

        await Assert.That(expression.Parts.Count).IsEqualTo(5);
        await Assert.That(((TextPart)expression.Parts[0]).Text).IsEqualTo("{x} ");
        var hole = (HolePart)expression.Parts[1];
        await Assert.That(hole.Format).IsEqualTo("yyyy-MM-dd");
        await Assert.That(((MemberSyntax)hole.Expression).Member).IsEqualTo("Created");
        await Assert.That(((TextPart)expression.Parts[2]).Text).IsEqualTo(" at \"");
        await Assert.That(((NameSyntax)((HolePart)expression.Parts[3]).Expression).Name).IsEqualTo("Api");
    }

    [Test]
    public async Task Single_quoted_strings_have_no_holes_or_escapes()
    {
        var expression = MeeplangParser.ParseExpression(@"'C:\data\{file}.txt'", out _);

        await Assert.That(((LiteralSyntax)expression!).Value).IsEqualTo(@"C:\data\{file}.txt");
    }

    [Test]
    public async Task Operators_follow_precedence()
    {
        var expression = MeeplangParser.ParseExpression("a or b and not c == -d * 2 + 1", out _)!;

        // a or (b and (not ((c) == (((-d) * 2) + 1))))
        var or = (BinarySyntax)expression;
        await Assert.That(or.Operator).IsEqualTo("or");
        var and = (BinarySyntax)or.Right;
        await Assert.That(and.Operator).IsEqualTo("and");
        var not = (UnarySyntax)and.Right;
        var equals = (BinarySyntax)not.Operand;
        await Assert.That(equals.Operator).IsEqualTo("==");
        var plus = (BinarySyntax)equals.Right;
        await Assert.That(plus.Operator).IsEqualTo("+");
        await Assert.That(((BinarySyntax)plus.Left).Operator).IsEqualTo("*");
    }

    [Test]
    public async Task Members_calls_indexes_and_type_tests_parse()
    {
        var call = (CallSyntax)MeeplangParser.ParseExpression("msg.items[0].name.Contains(\"x\")", out _)!;
        var test = (IsSyntax)MeeplangParser.ParseExpression("msg is not ErrorMessage", out _)!;

        await Assert.That(((MemberSyntax)call.Callee).Member).IsEqualTo("Contains");
        await Assert.That(((MemberSyntax)((MemberSyntax)call.Callee).Target).Target).IsTypeOf<IndexSyntax>();
        await Assert.That(test.Negated).IsTrue();
        await Assert.That(test.TypeName).IsEqualTo("ErrorMessage");
    }
}
