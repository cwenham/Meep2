namespace Meep2.Meeplang.Editing;

/// <summary>
/// One change to a text: what's between two places in it is replaced. Places count lines and characters from 0, and
/// characters in UTF-16 code units, as the Language Server Protocol does by default, so a change is an LSP
/// <c>TextEdit</c>.
/// </summary>
/// <param name="StartLine">The line it starts on.</param>
/// <param name="StartCharacter">Where on that line it starts.</param>
/// <param name="EndLine">The line it ends on.</param>
/// <param name="EndCharacter">Where on that line it ends: just after the last character replaced.</param>
/// <param name="NewText">What goes in its place.</param>
public sealed record TextChange(int StartLine, int StartCharacter, int EndLine, int EndCharacter, string NewText);

/// <summary>
/// The changes that turn one text into another, touching as little as they can, so an editor that applies them keeps
/// what it knows about the rest of the text (its folds, marks and cursors).
/// </summary>
/// <remarks>
/// Lines are compared first, with Myers' algorithm. Where as many lines replace as many, each line that differs is a
/// change of its own, trimmed to the characters that differ, so changing a value replaces just the value. Where lines
/// are added or removed, the change is whole lines, as a diff shows them. Each change is placed in the old text, and
/// none overlap, so they can be applied together, as LSP applies a list of <c>TextEdit</c>s.
/// </remarks>
public static class TextChanges
{
    // Past this many cells of Myers' trace, the lines between the first and last that differ are one change.
    private const long TraceLimit = 20_000_000;

    /// <summary>The changes that turn <paramref name="old"/> into <paramref name="next"/>, in the order they're in the text.</summary>
    public static IReadOnlyList<TextChange> Between(string old, string next)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(next);
        if (old == next)
            return [];

        // Each line keeps a \r before its \n. Counted as if both texts ended with a \n, so every line has one.
        var before = old.Split('\n');
        var after = next.Split('\n');
        var oldStarts = Starts(before);
        var nextStarts = Starts(after);
        var changes = new List<TextChange>();
        foreach (var (a0, a1, b0, b1) in Hunks(before, after))
        {
            if (a1 - a0 == b1 - b0)
            {
                for (var i = 0; i < a1 - a0; i++)
                {
                    if (before[a0 + i] != after[b0 + i])
                        changes.Add(WithinLine(old, oldStarts[a0 + i], before[a0 + i], after[b0 + i]));
                }
                continue;
            }

            // Whole lines, in the texts with a \n after the last line.
            var start = oldStarts[a0];
            var end = oldStarts[a1];
            var text = (next + "\n")[nextStarts[b0]..nextStarts[b1]];
            if (end > old.Length)
            {
                // The change reaches the end, so the \n counted after the old text's last line is in it. The new
                // text's is at the end of the new lines, or, when there are none, just before the change.
                end = old.Length;
                if (text.Length == 0)
                {
                    start--; // the line before the change is the new last line, so its \n goes too
                }
                else
                {
                    text = text[..^1];
                    if (start > old.Length)
                    {
                        start = old.Length;
                        text = "\n" + text;
                    }
                }
            }
            changes.Add(Change(old, start, end, text));
        }
        return changes;
    }

    /// <summary>A text with changes made, as an editor makes them: each placed in the text as it was.</summary>
    public static string Apply(string text, IEnumerable<TextChange> changes)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(changes);
        var starts = Starts(text.Split('\n'));
        int Offset(int line, int character) => Math.Min(starts[Math.Min(line, starts.Length - 1)] + character, text.Length);
        var result = text;
        foreach (var change in changes.OrderByDescending(c => c.StartLine).ThenByDescending(c => c.StartCharacter))
        {
            var start = Offset(change.StartLine, change.StartCharacter);
            var end = Offset(change.EndLine, change.EndCharacter);
            result = result[..start] + change.NewText + result[end..];
        }
        return result;
    }

    // Where each line starts, as offsets into the text with a \n after its last line, and one more for the end.
    private static int[] Starts(string[] lines)
    {
        var starts = new int[lines.Length + 1];
        for (var i = 0; i < lines.Length; i++)
            starts[i + 1] = starts[i] + lines[i].Length + 1;
        return starts;
    }

    // A change within one line: just the characters that differ.
    private static TextChange WithinLine(string old, int lineStart, string was, string now)
    {
        var prefix = 0;
        while (prefix < was.Length && prefix < now.Length && was[prefix] == now[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < was.Length - prefix && suffix < now.Length - prefix && was[^(suffix + 1)] == now[^(suffix + 1)])
            suffix++;

        // Not between the halves of a surrogate pair.
        if (prefix > 0 && char.IsHighSurrogate(was[prefix - 1]))
            prefix--;
        if (suffix > 0 && char.IsLowSurrogate(was[^suffix]))
            suffix--;
        return Change(old, lineStart + prefix, lineStart + was.Length - suffix, now[prefix..(now.Length - suffix)]);
    }

    private static TextChange Change(string text, int start, int end, string newText)
    {
        var (startLine, startCharacter) = Position(text, start);
        var (endLine, endCharacter) = Position(text, end);
        return new TextChange(startLine, startCharacter, endLine, endCharacter, newText);
    }

    private static (int Line, int Character) Position(string text, int offset)
    {
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }
        return (line, offset - lineStart);
    }

    // The stretches of lines that differ: old lines [A0, A1) became new lines [B0, B1).
    private static List<(int A0, int A1, int B0, int B1)> Hunks(string[] a, string[] b)
    {
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)])
            suffix++;
        var n = a.Length - prefix - suffix;
        var m = b.Length - prefix - suffix;
        if (n == 0 && m == 0)
            return [];
        if (n == 0 || m == 0)
            return [(prefix, prefix + n, prefix, prefix + m)];

        // Which lines of the middle are the same in both, by Myers' algorithm, walked back from the end.
        var same = Myers(a, b, prefix, n, m);
        if (same is null)
            return [(prefix, prefix + n, prefix, prefix + m)];
        var hunks = new List<(int, int, int, int)>();
        int x = 0, y = 0;
        foreach (var (sx, sy) in same.Append((n, m)))
        {
            if (sx > x || sy > y)
                hunks.Add((prefix + x, prefix + sx, prefix + y, prefix + sy));
            (x, y) = (sx + 1, sy + 1);
        }
        return hunks;
    }

    // The pairs of lines (counting from the start of the middle) that are the same, in order, or null if working
    // them out would take too long.
    private static List<(int X, int Y)>? Myers(string[] a, string[] b, int offset, int n, int m)
    {
        var max = n + m;
        var v = new int[2 * max + 2];
        var trace = new List<int[]>();
        var found = false;
        for (var d = 0; d <= max && !found; d++)
        {
            if ((long)(d + 1) * v.Length > TraceLimit)
                return null;
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[max + k - 1] < v[max + k + 1]) ? v[max + k + 1] : v[max + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[offset + x] == b[offset + y])
                {
                    x++;
                    y++;
                }
                v[max + k] = x;
                if (x >= n && y >= m)
                {
                    found = true;
                    break;
                }
            }
        }

        var same = new List<(int, int)>();
        int cx = n, cy = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var before = trace[d];
            var k = cx - cy;
            var previousK = k == -d || (k != d && before[max + k - 1] < before[max + k + 1]) ? k + 1 : k - 1;
            var previousX = d == 0 ? 0 : before[max + previousK];
            var previousY = previousX - previousK;
            if (d == 0)
                (previousX, previousY) = (0, 0);
            while (cx > previousX && cy > previousY)
            {
                cx--;
                cy--;
                same.Add((cx, cy));
            }
            (cx, cy) = (previousX, previousY);
        }
        same.Reverse();
        return same;
    }
}
