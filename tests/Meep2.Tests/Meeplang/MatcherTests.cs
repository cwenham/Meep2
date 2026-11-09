using Meep2.Meeplang.Editing;
using Meep2.Tests.Support;

namespace Meep2.Tests.Meeplang;

/// <summary>Which modules of a file's new text are which of its old: name, then structure, then position (Q61).</summary>
public class MatcherTests
{
    private static IReadOnlyList<ModuleMatch> Match(string old, string next) =>
        ModuleMatcher.Match(Script.Parse(old), Script.Parse(next));

    private static string Describe(IReadOnlyList<ModuleMatch> matches) => Wait.Join(matches.Select(m =>
        $"{m.Old?.VerbSpan.Line.ToString() ?? "-"}>{m.Next?.VerbSpan.Line.ToString() ?? "-"} {m.By}{(m.ParametersChanged ? " changed" : "")}{(m.Moved ? " moved" : "")}"));

    [Test]
    public async Task A_named_module_is_found_wherever_it_moves()
    {
        var matches = Match("""
            Interval 1s
                Where msg.Count > 1 as Odd
                Where msg.Count > 2 as Big
            """, """
            Interval 1s
                Where msg.Count > 2 as Big
                    Where msg.Count > 3 as Odd
            """);

        await Assert.That(Describe(matches)).IsEqualTo("1>1 Structure,3>2 Name,2>3 Name changed moved");
    }

    [Test]
    public async Task Of_two_siblings_with_one_verb_the_one_with_the_same_parameters_is_chosen()
    {
        var matches = Match("""
            Interval 1s
                WriteLine "a"
                WriteLine "b"
            """, """
            Interval 1s
                WriteLine "b"
                WriteLine "c"
            """);

        // "b" keeps its module though it moved up; "c" takes the one that was "a".
        await Assert.That(Describe(matches)).IsEqualTo("1>1 Structure,3>2 Structure,2>3 Structure changed");
    }

    [Test]
    public async Task A_module_whose_verb_changed_is_a_new_module()
    {
        var matches = Match("""
            Interval 1s as Clock
                WriteLine "a"
            """, """
            Once as Clock
                WriteLine "a"
            """);

        // The new source is new; the WriteLine under it keeps its module, matched by position.
        await Assert.That(Describe(matches)).IsEqualTo("->1 Nothing,2>2 Position moved,1>- Nothing");
    }

    [Test]
    public async Task Position_matches_what_structure_cant_and_lets_what_is_under_it_match_by_structure()
    {
        var matches = Match("""
            Interval 1s
                Tally
                    Sink
            """, """
            Interval 1s
                Where msg.Count > 0
                    Tally
                        Sink
            """);

        await Assert.That(Describe(matches)).IsEqualTo("1>1 Structure,->2 Nothing,2>3 Position moved,3>4 Structure");
    }

    [Test]
    public async Task A_renamed_module_is_found_by_structure()
    {
        var matches = Match("""
            Interval 1s
                Tally as Counter
            """, """
            Interval 1s
                Tally as Counted
            """);

        await Assert.That(Describe(matches)).IsEqualTo("1>1 Structure,2>2 Structure");
    }
}
