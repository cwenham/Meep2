using System.Text;
using Meep2.SDK.Messages;

namespace Meep2.SDK.Security;

/// <summary>
/// A permission and what it requires: which authorities may confer it, and which other permissions a credential
/// must also have. The trust store's definitions are these, and a pipeline's <c>#permission</c> directive makes one
/// that narrows a definition (Q257).
/// </summary>
/// <remarks>
/// <para>
/// Anyone can make one; it means nothing until <c>Verify.Permission</c> checks it is well defined (and, when asked,
/// met), and emits a <see cref="VerifiedMessage"/> saying so.
/// </para>
/// <para>
/// Written as a directive, one to a line, as the trust store's <c>permissions.meep</c> has them:
/// <code>#permission WriteNotes authority=home requires=ReadNotes requires=meep.files.write:~/notes/**</code>
/// </para>
/// </remarks>
public sealed record Permission : Message
{
    /// <summary>The directive that defines or narrows a permission.</summary>
    public const string Directive = "#permission";

    /// <summary>Makes a permission with no requirements.</summary>
    public Permission(PermissionName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <summary>The permission's name and scope.</summary>
    public PermissionName Name { get; init; }

    /// <summary>
    /// The authorities that may confer it, by their names in the trust store. Empty means any authority the store
    /// trusts.
    /// </summary>
    public IReadOnlyList<string> Authorities { get; init; } = [];

    /// <summary>Other permissions a credential must also have for this one to be met.</summary>
    public IReadOnlyList<PermissionName> Requires { get; init; } = [];

    /// <summary>
    /// Reads a <c>#permission</c> directive: the name, with a scope if it has one, then any number of
    /// <c>authority=name</c> and <c>requires=permission</c>, in any order. A value with spaces is written in double
    /// quotes. <c>//</c> starts a comment. Throws <see cref="FormatException"/>, saying why, if it can't.
    /// </summary>
    public static Permission ParseDirective(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var words = DirectiveWords.Split(line);
        if (words.Count == 0 || words[0] != Directive)
            throw new FormatException($"A permission is written as: {Directive} Name [authority=name] [requires=permission]. This isn't one.");
        if (words.Count < 2 || words[1].Contains('=', StringComparison.Ordinal))
            throw new FormatException($"{Directive} needs the permission's name next, as in {Directive} ReadNotes.");
        var authorities = new List<string>();
        var requires = new List<PermissionName>();
        foreach (var word in words.Skip(2))
        {
            var equals = word.IndexOf('=', StringComparison.Ordinal);
            var (key, value) = equals < 0 ? (word, "") : (word[..equals], word[(equals + 1)..]);
            if (value.Length == 0)
                throw new FormatException($"{word} isn't something {Directive} takes. It takes authority=name and requires=permission.");
            switch (key)
            {
                case "authority" when IsAuthorityName(value):
                    if (!authorities.Contains(value, StringComparer.Ordinal))
                        authorities.Add(value);
                    break;
                case "authority":
                    throw new FormatException($"{value} can't be an authority's name: it has no spaces, quotes or # in it.");
                case "requires":
                    var required = PermissionName.Parse(value);
                    if (!requires.Contains(required))
                        requires.Add(required);
                    break;
                default:
                    throw new FormatException($"{key} isn't something {Directive} takes. It takes authority=name and requires=permission.");
            }
        }
        return new Permission(PermissionName.Parse(words[1])) { Authorities = authorities, Requires = requires };
    }

    /// <summary>Whether <paramref name="name"/> can name an authority in the trust store: no spaces, quotes or <c>#</c>.</summary>
    public static bool IsAuthorityName(string? name) =>
        !string.IsNullOrEmpty(name) && !name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '#' or '=');

    /// <summary>The permission as a directive, as <see cref="ParseDirective"/> reads it.</summary>
    public string ToDirective()
    {
        var text = new StringBuilder(Directive).Append(' ').Append(DirectiveWords.Quote(Name.ToString()));
        foreach (var authority in Authorities)
            text.Append(" authority=").Append(authority);
        foreach (var required in Requires)
            text.Append(" requires=").Append(DirectiveWords.Quote(required.ToString()));
        return text.ToString();
    }

    /// <summary>
    /// This permission, as a pipeline's directive narrows it: the directive's name and scope, its authorities if it
    /// names any (or this one's if not), and the requirements of both. Whether the result is in fact no wider than
    /// this one is for <see cref="WiderThan"/> to say, since a directive could name a wider scope or another authority.
    /// </summary>
    public Permission NarrowedBy(Permission directive)
    {
        ArgumentNullException.ThrowIfNull(directive);
        return new Permission(directive.Name)
        {
            Authorities = directive.Authorities.Count > 0 ? directive.Authorities : Authorities,
            Requires = [.. Requires, .. directive.Requires.Where(required => !Requires.Contains(required))],
        };
    }

    /// <summary>
    /// The ways this permission is looser than <paramref name="definition"/>, its definition, as
    /// <see cref="VerificationFailureCode.WiderThanDefinition"/> failures, or none if it's as strict or stricter: a scope
    /// within the definition's, only authorities the definition allows, and every requirement the definition has.
    /// </summary>
    public IReadOnlyList<VerificationFailure> WiderThan(Permission definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var failures = new List<VerificationFailure>();
        void Wider(string reason) => failures.Add(new VerificationFailure(VerificationFailureCode.WiderThanDefinition, reason));

        if (definition.Name.Name != Name.Name)
            Wider($"{Name.Name} isn't {definition.Name.Name}, the permission it's checked against.");
        else if (!definition.Name.Covers(Name))
            Wider($"{Name} reaches outside {definition.Name}, as {Name.Name} is defined.");
        if (definition.Authorities.Count > 0)
        {
            if (Authorities.Count == 0)
                Wider($"{Name.Name} may only be conferred by {string.Join(", ", definition.Authorities)}, not by any authority.");
            foreach (var authority in Authorities.Where(a => !definition.Authorities.Contains(a, StringComparer.Ordinal)))
                Wider($"{authority} may not confer {Name.Name}: only {string.Join(", ", definition.Authorities)} may.");
        }
        // A requirement is kept if one at least as wide is required: holding that means holding the definition's.
        foreach (var required in definition.Requires.Where(r => !Requires.Any(mine => mine.Covers(r))))
            Wider($"{Name.Name} requires {required} as well, and this leaves it out.");
        return failures;
    }

    /// <inheritdoc/>
    public override string ToText() => Name.ToString();
}

/// <summary>The words of a directive line: split at spaces, with double-quoted text kept whole, and <c>//</c> starting a comment.</summary>
internal static class DirectiveWords
{
    public static List<string> Split(string line)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '\\' && i + 1 < line.Length && line[i + 1] is '"' or '\\')
                    word.Append(line[++i]);
                else if (c == '"')
                    quoted = false;
                else
                    word.Append(c);
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (inWord)
                    words.Add(word.ToString());
                word.Clear();
                inWord = false;
                continue;
            }
            if (!inWord && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                break;
            inWord = true;
            if (c == '"')
                quoted = true;
            else
                word.Append(c);
        }
        if (quoted)
            throw new FormatException("A quote isn't closed.");
        if (inWord)
            words.Add(word.ToString());
        return words;
    }

    /// <summary>The text as one word: in double quotes if it has spaces, quotes or starts like a comment.</summary>
    public static string Quote(string text) =>
        text.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\') || text.StartsWith("//", StringComparison.Ordinal)
            ? "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : text;
}
