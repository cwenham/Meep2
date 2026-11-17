using Meep2.Meeplang.Editing;

namespace Meep2.Tests.Meeplang;

/// <summary>The changes that turn one text into another, as LSP <c>TextEdit</c>s, for <c>meep/applyEdits</c>.</summary>
public class TextChangesTests
{
    // Applies changes as an editor does, placing each in the text as it was: worked out here, apart from TextChanges.
    private static string Applied(string text, IReadOnlyList<TextChange> changes)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                lineStarts.Add(i + 1);
        }
        var result = text;
        TextChange? later = null;
        foreach (var change in changes.Reverse())
        {
            var start = lineStarts[change.StartLine] + change.StartCharacter;
            var end = lineStarts[change.EndLine] + change.EndCharacter;
            if (start > end || end > text.Length)
                throw new InvalidOperationException($"{change} isn't a range in the text.");
            if (later is not null && end > lineStarts[later.StartLine] + later.StartCharacter)
                throw new InvalidOperationException($"{change} overlaps {later}, or they're out of order.");
            result = result[..start] + change.NewText + result[end..];
            later = change;
        }
        return result;
    }

    [Test]
    [Arguments("", "")]
    [Arguments("", "a")]
    [Arguments("a", "")]
    [Arguments("a\nb", "a\nb\nc")]
    [Arguments("a\nb\nc", "a\nb")]
    [Arguments("a\nb\n", "a\nb")]
    [Arguments("a\nb", "a\nb\n")]
    [Arguments("a\nb\nc", "b\nc")]
    [Arguments("a\nb\nc", "x\na\nb\nc")]
    [Arguments("a\nb\nc\nd\ne", "a\nB\nc\nd\nE")]
    [Arguments("a\nb\nc\nd\ne", "a\nc\nx\ny\ne")]
    [Arguments("one\ntwo\nthree", "three\ntwo\none")]
    [Arguments("a\r\nb\r\nc", "a\r\nb2\r\nnew\r\nc")]
    [Arguments("a\r\nb\r\nc", "a\r\nb")]
    [Arguments("a\r\nb\r\nc\r\n", "a\r\nc\r\n")]
    [Arguments("x 😀 y", "x 😁 y")]
    [Arguments("\n\n\n", "\n\nz\n")]
    public async Task Applying_the_changes_gives_the_new_text(string old, string next)
    {
        var changes = TextChanges.Between(old, next);

        await Assert.That(Applied(old, changes)).IsEqualTo(next);
        await Assert.That(TextChanges.Apply(old, changes)).IsEqualTo(next);
    }

    [Test]
    public async Task A_changed_value_is_just_the_value()
    {
        const string Old = "Interval 30s\n    Fetch Site as Check   // the request\n        timeout = 10s\n        WriteLine \"up\"\n";
        var next = Old.Replace("timeout = 10s", "timeout = 5s", StringComparison.Ordinal);

        var change = TextChanges.Between(Old, next).Single();

        await Assert.That(change).IsEqualTo(new TextChange(2, 18, 2, 20, "5"));
    }

    [Test]
    public async Task Lines_added_or_removed_are_whole_lines_and_changes_far_apart_are_apart()
    {
        var lines = Enumerable.Range(1, 400).Select(i => $"line {i}").ToList();
        var old = string.Join("\n", lines);
        lines.Insert(10, "    added");
        lines[300] = "line 300 changed";
        lines.RemoveAt(398);
        var next = string.Join("\n", lines);

        var changes = TextChanges.Between(old, next);

        await Assert.That(changes.Count).IsEqualTo(3);
        await Assert.That(changes[0]).IsEqualTo(new TextChange(10, 0, 10, 0, "    added\n"));
        await Assert.That(changes[1]).IsEqualTo(new TextChange(299, 8, 299, 8, " changed"));
        await Assert.That(changes[2]).IsEqualTo(new TextChange(397, 0, 398, 0, ""));
        await Assert.That(Applied(old, changes)).IsEqualTo(next);
    }

    [Test]
    public async Task A_text_rewritten_throughout_still_comes_out_right()
    {
        var random = new Random(186);
        for (var round = 0; round < 200; round++)
        {
            var old = string.Join("\n", Enumerable.Range(0, random.Next(0, 30)).Select(_ => "abc"[random.Next(3)].ToString()));
            var next = string.Join("\n", Enumerable.Range(0, random.Next(0, 30)).Select(_ => "abcd"[random.Next(4)].ToString()));
            if (random.Next(2) == 0)
                next += "\n";

            await Assert.That(Applied(old, TextChanges.Between(old, next))).IsEqualTo(next);
        }
    }
}
