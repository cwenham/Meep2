using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Meep2.Modules;

/// <summary>
/// A JSONPath query (RFC 9535), as used by <see cref="Extract"/>. Supports a common subset:
/// <list type="bullet">
/// <item><c>$</c>, the root</item>
/// <item><c>.name</c> and <c>['name']</c>, a member</item>
/// <item><c>[2]</c> and <c>[-1]</c>, an array element counting from the start or the end</item>
/// <item><c>[*]</c> and <c>.*</c>, every element or member value</item>
/// <item><c>[0:5]</c> and <c>[::2]</c>, slices</item>
/// <item><c>['a','b']</c> and <c>[0,2]</c>, several at once</item>
/// <item><c>..name</c> and <c>..*</c>, searching at every depth</item>
/// </list>
/// Filters such as <c>[?@.price &lt; 10]</c> aren't supported; use <c>Where</c> on the results instead.
/// </summary>
public sealed class JsonPath : IParsable<JsonPath>
{
    private readonly Segment[] _segments;

    private JsonPath(string text, Segment[] segments)
    {
        Text = text;
        _segments = segments;
    }

    /// <summary>The query as written.</summary>
    public string Text { get; }

    /// <summary>Parses a query.</summary>
    /// <exception cref="FormatException">The query isn't valid, or uses a feature that isn't supported.</exception>
    public static JsonPath Parse(string text) => Parse(text, provider: null);

    /// <inheritdoc/>
    public static JsonPath Parse(string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new JsonPath(s, new Reader(s).ReadPath());
    }

    /// <inheritdoc/>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out JsonPath result)
    {
        try
        {
            result = s is null ? null : Parse(s, provider);
            return result is not null;
        }
        catch (FormatException)
        {
            result = null;
            return false;
        }
    }

    /// <summary>Finds every value the query matches, in document order.</summary>
    public IReadOnlyList<JsonElement> Select(JsonElement root)
    {
        List<JsonElement> current = [root];
        foreach (var segment in _segments)
        {
            var next = new List<JsonElement>();
            foreach (var node in current)
            {
                if (segment.Descendants)
                {
                    foreach (var descendant in SelfAndDescendants(node))
                        Apply(segment.Selectors, descendant, next);
                }
                else
                {
                    Apply(segment.Selectors, node, next);
                }
            }
            current = next;
        }
        return current;
    }

    /// <inheritdoc/>
    public override string ToString() => Text;

    private static void Apply(Selector[] selectors, JsonElement node, List<JsonElement> results)
    {
        foreach (var selector in selectors)
            selector.Select(node, results);
    }

    private static IEnumerable<JsonElement> SelfAndDescendants(JsonElement node)
    {
        yield return node;
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var member in node.EnumerateObject())
                {
                    foreach (var descendant in SelfAndDescendants(member.Value))
                        yield return descendant;
                }
                break;
            case JsonValueKind.Array:
                foreach (var element in node.EnumerateArray())
                {
                    foreach (var descendant in SelfAndDescendants(element))
                        yield return descendant;
                }
                break;
        }
    }

    private sealed record Segment(Selector[] Selectors, bool Descendants);

    private abstract record Selector
    {
        public abstract void Select(JsonElement node, List<JsonElement> results);
    }

    private sealed record NameSelector(string Name) : Selector
    {
        public override void Select(JsonElement node, List<JsonElement> results)
        {
            if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(Name, out var value))
                results.Add(value);
        }
    }

    private sealed record WildcardSelector : Selector
    {
        public override void Select(JsonElement node, List<JsonElement> results)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var member in node.EnumerateObject())
                        results.Add(member.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var element in node.EnumerateArray())
                        results.Add(element);
                    break;
            }
        }
    }

    private sealed record IndexSelector(int Index) : Selector
    {
        public override void Select(JsonElement node, List<JsonElement> results)
        {
            if (node.ValueKind != JsonValueKind.Array)
                return;
            var length = node.GetArrayLength();
            var index = Index < 0 ? length + Index : Index;
            if (index >= 0 && index < length)
                results.Add(node[index]);
        }
    }

    private sealed record SliceSelector(int? Start, int? End, int Step) : Selector
    {
        public override void Select(JsonElement node, List<JsonElement> results)
        {
            if (node.ValueKind != JsonValueKind.Array || Step == 0)
                return;
            var length = node.GetArrayLength();
            static int Normalize(int value, int length) => value >= 0 ? value : length + value;
            if (Step > 0)
            {
                var start = Math.Clamp(Normalize(Start ?? 0, length), 0, length);
                var end = Math.Clamp(Normalize(End ?? length, length), 0, length);
                for (var i = start; i < end; i += Step)
                    results.Add(node[i]);
            }
            else
            {
                var start = Math.Clamp(Normalize(Start ?? length - 1, length), -1, length - 1);
                var end = Math.Clamp(Normalize(End ?? -length - 1, length), -1, length - 1);
                for (var i = start; i > end; i += Step)
                    results.Add(node[i]);
            }
        }
    }

    // Reads the query text into segments.
    private sealed class Reader(string text)
    {
        private int _position;

        public Segment[] ReadPath()
        {
            SkipSpaces();
            if (!TryTake('$'))
                throw Error("A JSONPath query starts with $");

            var segments = new List<Segment>();
            while (true)
            {
                SkipSpaces();
                if (_position >= text.Length)
                    return [.. segments];

                if (TryTake(".."))
                {
                    if (TryTake('['))
                        segments.Add(new Segment(ReadBracketed(), Descendants: true));
                    else
                        segments.Add(new Segment([ReadDotSelector()], Descendants: true));
                }
                else if (TryTake('.'))
                {
                    segments.Add(new Segment([ReadDotSelector()], Descendants: false));
                }
                else if (TryTake('['))
                {
                    segments.Add(new Segment(ReadBracketed(), Descendants: false));
                }
                else
                {
                    throw Error($"Unexpected '{text[_position]}'");
                }
            }
        }

        private Selector ReadDotSelector()
        {
            if (TryTake('*'))
                return new WildcardSelector();
            var start = _position;
            while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] is '_' || text[_position] > 127))
                _position++;
            if (_position == start || char.IsDigit(text[start]))
                throw Error("Expected a member name after '.'");
            return new NameSelector(text[start.._position]);
        }

        private Selector[] ReadBracketed()
        {
            var selectors = new List<Selector>();
            while (true)
            {
                SkipSpaces();
                selectors.Add(ReadSelector());
                SkipSpaces();
                if (TryTake(']'))
                    return [.. selectors];
                if (!TryTake(','))
                    throw Error("Expected ',' or ']'");
            }
        }

        private Selector ReadSelector()
        {
            if (_position >= text.Length)
                throw Error("The query ends inside [ ]");
            var c = text[_position];
            if (c == '*')
            {
                _position++;
                return new WildcardSelector();
            }
            if (c is '\'' or '"')
                return new NameSelector(ReadQuoted());
            if (c == '?')
                throw Error("Filters ([?…]) aren't supported; use Where on the results instead");

            var first = ReadOptionalInteger();
            SkipSpaces();
            if (!TryTake(':'))
                return first is { } index ? new IndexSelector(index) : throw Error("Expected an index, a name or *");

            SkipSpaces();
            var end = ReadOptionalInteger();
            SkipSpaces();
            var step = 1;
            if (TryTake(':'))
            {
                SkipSpaces();
                step = ReadOptionalInteger() ?? 1;
            }
            return new SliceSelector(first, end, step);
        }

        private int? ReadOptionalInteger()
        {
            var start = _position;
            if (_position < text.Length && text[_position] == '-')
                _position++;
            while (_position < text.Length && char.IsAsciiDigit(text[_position]))
                _position++;
            if (_position == start)
                return null;
            if (!int.TryParse(text.AsSpan(start, _position - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                throw Error("Expected a whole number");
            return value;
        }

        private string ReadQuoted()
        {
            var quote = text[_position++];
            var result = new StringBuilder();
            while (_position < text.Length)
            {
                var c = text[_position++];
                if (c == quote)
                    return result.ToString();
                if (c == '\\' && _position < text.Length)
                {
                    var escaped = text[_position++];
                    result.Append(escaped switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        _ => escaped,
                    });
                    continue;
                }
                result.Append(c);
            }
            throw Error("A quoted name isn't closed");
        }

        private bool TryTake(char c)
        {
            if (_position < text.Length && text[_position] == c)
            {
                _position++;
                return true;
            }
            return false;
        }

        private bool TryTake(string s)
        {
            if (string.CompareOrdinal(text, _position, s, 0, s.Length) == 0)
            {
                _position += s.Length;
                return true;
            }
            return false;
        }

        private void SkipSpaces()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
                _position++;
        }

        private FormatException Error(string problem) =>
            new($"{problem} in the JSONPath query {text} (at character {_position + 1}).");
    }
}
