using System.Globalization;
using System.Text;

namespace Meep2.Runtime.Verifying;

/// <summary>
/// The part of HTTP's structured fields (RFC 8941) that request signatures use: a dictionary whose members are items
/// or inner lists, with parameters. Enough to read <c>Signature-Input</c>, <c>Signature</c> and <c>Content-Digest</c>,
/// and to write a signature's parameters back as RFC 9421 signs them.
/// </summary>
internal static class StructuredFields
{
    /// <summary>
    /// Reads a dictionary. Throws <see cref="FormatException"/>, saying where, if it isn't one.
    /// </summary>
    public static Dictionary<string, SfMember> ParseDictionary(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var reader = new Reader(text);
        var members = new Dictionary<string, SfMember>(StringComparer.Ordinal);
        reader.SkipSpaces();
        if (reader.AtEnd)
            return members;
        while (true)
        {
            var key = reader.Key();
            SfMember member;
            if (reader.TryTake('='))
            {
                member = reader.Peek() == '(' ? reader.InnerList() : reader.Item();
            }
            else
            {
                member = new SfMember(new SfItem(true), null, reader.Parameters());
            }
            members[key] = member;
            reader.SkipOptionalSpaces();
            if (reader.AtEnd)
                return members;
            reader.Expect(',');
            reader.SkipOptionalSpaces();
            if (reader.AtEnd)
                throw new FormatException("it ends with a comma");
        }
    }

    /// <summary>Writes an inner list and its parameters, as RFC 9421's <c>@signature-params</c> line has them.</summary>
    public static string Serialize(SfMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var text = new StringBuilder();
        if (member.InnerList is { } list)
        {
            text.Append('(');
            for (var i = 0; i < list.Count; i++)
            {
                if (i > 0)
                    text.Append(' ');
                Write(text, list[i].Item!);
                WriteParameters(text, list[i].Parameters);
            }
            text.Append(')');
        }
        else
        {
            Write(text, member.Item!);
        }
        WriteParameters(text, member.Parameters);
        return text.ToString();
    }

    private static void WriteParameters(StringBuilder text, IReadOnlyList<KeyValuePair<string, SfItem>> parameters)
    {
        foreach (var (key, value) in parameters)
        {
            text.Append(';').Append(key);
            if (value.Value is not true)
                Write(text.Append('='), value);
        }
    }

    private static void Write(StringBuilder text, SfItem item)
    {
        switch (item.Value)
        {
            case long number:
                text.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case bool flag:
                text.Append(flag ? "?1" : "?0");
                break;
            case byte[] bytes:
                text.Append(':').Append(Convert.ToBase64String(bytes)).Append(':');
                break;
            case string word when item.IsToken:
                text.Append(word);
                break;
            case string quoted:
                text.Append('"').Append(quoted.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
                break;
        }
    }

    private ref struct Reader(string text)
    {
        private int _at;

        public readonly bool AtEnd => _at >= text.Length;

        public readonly char Peek() => AtEnd ? '\0' : text[_at];

        public bool TryTake(char c)
        {
            if (Peek() != c)
                return false;
            _at++;
            return true;
        }

        public void Expect(char c)
        {
            if (!TryTake(c))
                throw new FormatException($"it should have {c} at {_at + 1}");
        }

        public void SkipSpaces()
        {
            while (Peek() == ' ')
                _at++;
        }

        public void SkipOptionalSpaces()
        {
            while (Peek() is ' ' or '\t')
                _at++;
        }

        public string Key()
        {
            var start = _at;
            if (!(char.IsAsciiLetterLower(Peek()) || Peek() == '*'))
                throw new FormatException($"it should have a key at {_at + 1}");
            while (char.IsAsciiLetterLower(Peek()) || char.IsAsciiDigit(Peek()) || Peek() is '_' or '-' or '.' or '*')
                _at++;
            return text[start.._at];
        }

        public SfMember InnerList()
        {
            Expect('(');
            var items = new List<SfMember>();
            while (true)
            {
                SkipSpaces();
                if (TryTake(')'))
                    return new SfMember(null, items, Parameters());
                items.Add(Item());
                if (Peek() is not (' ' or ')'))
                    throw new FormatException($"it should have a space or ) at {_at + 1}");
            }
        }

        public SfMember Item() => new(BareItem(), null, Parameters());

        public List<KeyValuePair<string, SfItem>> Parameters()
        {
            var parameters = new List<KeyValuePair<string, SfItem>>();
            while (TryTake(';'))
            {
                SkipSpaces();
                var key = Key();
                var value = TryTake('=') ? BareItem() : new SfItem(true);
                parameters.RemoveAll(p => p.Key == key);
                parameters.Add(new(key, value));
            }
            return parameters;
        }

        private SfItem BareItem()
        {
            var c = Peek();
            if (c == '"')
                return new SfItem(QuotedString());
            if (c == ':')
                return new SfItem(ByteSequence());
            if (c == '?')
            {
                _at++;
                return TryTake('1') ? new SfItem(true) : TryTake('0') ? new SfItem(false) : throw new FormatException($"it has a bad boolean at {_at + 1}");
            }
            if (c == '-' || char.IsAsciiDigit(c))
                return new SfItem(Integer());
            if (char.IsAsciiLetter(c) || c == '*')
                return new SfItem(Token(), IsToken: true);
            throw new FormatException($"it has something that isn't an item at {_at + 1}");
        }

        private string QuotedString()
        {
            Expect('"');
            var value = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                    throw new FormatException("a quoted string isn't closed");
                var c = text[_at++];
                if (c == '"')
                    return value.ToString();
                if (c == '\\')
                {
                    if (AtEnd || text[_at] is not ('"' or '\\'))
                        throw new FormatException($"it has a bad escape at {_at}");
                    c = text[_at++];
                }
                else if (c < 0x20 || c > 0x7e)
                {
                    throw new FormatException($"a quoted string has a character it can't at {_at}");
                }
                value.Append(c);
            }
        }

        private byte[] ByteSequence()
        {
            Expect(':');
            var end = text.IndexOf(':', _at);
            if (end < 0)
                throw new FormatException("a byte sequence isn't closed");
            var base64 = text[_at..end];
            _at = end + 1;
            try
            {
                return Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                throw new FormatException("a byte sequence isn't valid base64");
            }
        }

        private long Integer()
        {
            var start = _at;
            TryTake('-');
            while (char.IsAsciiDigit(Peek()))
                _at++;
            if (Peek() == '.')
                throw new FormatException($"it has a decimal where it should have a whole number at {start + 1}");
            return _at - start is > 0 and <= 16 && long.TryParse(text.AsSpan(start, _at - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                ? number
                : throw new FormatException($"it has a bad number at {start + 1}");
        }

        private string Token()
        {
            var start = _at;
            while (!AtEnd && (char.IsAsciiLetterOrDigit(Peek()) || "!#$%&'*+-.^_`|~:/".Contains(Peek(), StringComparison.Ordinal)))
                _at++;
            return text[start.._at];
        }
    }
}

/// <summary>A structured field's bare item: a whole number, a string or token, bytes, or a boolean.</summary>
/// <param name="Value">A <see cref="long"/>, <see cref="string"/>, <see cref="byte"/> array or <see cref="bool"/>.</param>
/// <param name="IsToken">Whether a string was written as a token rather than in quotes.</param>
internal sealed record SfItem(object Value, bool IsToken = false);

/// <summary>A dictionary's member or an inner list's item: an item or an inner list, with parameters.</summary>
/// <param name="Item">The item, or null for an inner list.</param>
/// <param name="InnerList">The inner list's items, or null for an item.</param>
/// <param name="Parameters">Its parameters, in order.</param>
internal sealed record SfMember(SfItem? Item, IReadOnlyList<SfMember>? InnerList, IReadOnlyList<KeyValuePair<string, SfItem>> Parameters)
{
    /// <summary>The parameter called <paramref name="key"/>'s value, or null.</summary>
    public object? Parameter(string key) => Parameters.FirstOrDefault(p => p.Key == key).Value?.Value;
}
