using System.Globalization;
using System.Text.RegularExpressions;
using Meep2.SDK.Security;

namespace Meep2.Security;

/// <summary>What <c>meep trust tidy</c> took out of the store, each as what it was and why it went.</summary>
/// <param name="Grants">The grants taken out of <c>grants</c>.</param>
/// <param name="Revocations">The lines taken out of <c>revoked</c>.</param>
public sealed record Tidied(IReadOnlyList<string> Grants, IReadOnlyList<string> Revocations);

/// <summary>
/// Decides what in the trust store's <c>grants</c> and <c>revoked</c> files is no longer needed (Q305), for
/// <see cref="TrustFolder.Tidy"/>. The store only grows otherwise, and each line is read and checked on every reload.
/// </summary>
/// <remarks>
/// <para>A grant goes when it has expired, has been revoked, or its credential has. A grant that falls only because
/// a credential that authorised it is revoked (Q301) stays, so that credential's revocation stays too.</para>
/// <para>
/// A revocation stays until what it revokes has expired, since a copy of a grant can still be presented, and an
/// expired credential or certificate fails anyway. That's when the comment above it says it's needed until, as Meep
/// writes it, or for a grant, when the grant recorded in <c>grants</c> expires. A revocation of a key, or one whose end
/// isn't known, stays for ever, as does a credential's while a grant in <c>grants</c> names it as an authoriser.
/// </para>
/// </remarks>
/// <param name="store">The store as it is before tidying.</param>
/// <param name="now">The moment to judge expiry at, in UTC.</param>
public sealed partial class StoreTidying(TrustStore store, DateTime now)
{
    /// <summary>The words Meep puts in a revocation's comment to say until when it's needed, as <c>needed until 2027-03-01T00:00:00Z</c>.</summary>
    public static string Until(DateTime end)
    {
        var utc = end.ToUniversalTime();
        var second = new DateTime((utc.Ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        return $"needed until {second.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}Z";
    }

    /// <summary>Until when a revocation's comment says it's needed, or null if it doesn't say.</summary>
    public static DateTime? UntilIn(IEnumerable<string> comments)
    {
        ArgumentNullException.ThrowIfNull(comments);
        foreach (var comment in comments)
        {
            var match = NeededUntil().Match(comment);
            if (match.Success && DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until))
                return until;
        }
        return null;
    }

    /// <summary>Why a line of <c>grants</c> can go, or null if it stays.</summary>
    public string? WhyGrantGoes(StoreLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (Read(line, Grant.Parse) is not { } grant)
            return null;
        var terms = grant.Terms;
        if (terms.ValidBefore is { } before && before <= now)
            return $"it expired at {Time(before)}";
        if (store.Revocations.Entries.Contains(new Revocation(RevocationKind.Grant, terms.Id.ToString())))
            return "it's revoked (the revocation stays until the grant would have expired)";
        if (store.Revocations.WhyRevoked(new CredentialName(terms.CredentialKey, terms.CredentialSerial, terms.CredentialAuthority)) is { } why)
            return $"its credential is revoked: {why}";
        return null;
    }

    /// <summary>
    /// Why a line of <c>revoked</c> can go, or null if it stays, given the grants left in <paramref name="grants"/> once
    /// they're tidied, whose authorisers' revocations stay.
    /// </summary>
    public Func<StoreLine, string?> WhyRevocationGoes(StoreLines grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var authorisers = grants.Entries
            .Select(line => Read(line, Grant.Parse))
            .OfType<Grant>()
            .SelectMany(grant => grant.Terms.AuthorisedBy)
            .Select(name => (name.Authority, name.Serial))
            .ToHashSet();
        return line =>
        {
            if (Read(line, Revocation.Parse) is not { } revocation)
                return null;
            var until = UntilIn(line.Comments);
            switch (revocation.Kind)
            {
                case RevocationKind.Grant:
                    until ??= store.Grants.FirstOrDefault(grant => grant.Terms.Id.ToString() == revocation.Value)?.Terms.ValidBefore;
                    return until <= now ? $"the grant expired at {Time(until.Value)}" : null;
                case RevocationKind.Serial:
                    if (until is not { } end || end > now)
                        return null;
                    return authorisers.Contains((revocation.Value, revocation.Serial)) ? null : $"the credential expired at {Time(end)}";
                case RevocationKind.Certificate:
                    return until <= now ? $"the certificate expired at {Time(until.Value)}" : null;
                default:
                    return null;
            }
        };
    }

    // A line's entry read, or null for a line with none, or one that can't be read, which stays.
    private static T? Read<T>(StoreLine line, Func<string, T> parse) where T : class
    {
        if (line.Entry is not { } entry)
            return null;
        try
        {
            return parse(entry.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Time(DateTime when) => when.ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"needed until (\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)Z")]
    private static partial Regex NeededUntil();
}

/// <summary>One entry of a store file such as <c>grants</c> or <c>revoked</c>, with the comment Meep wrote above it; or
/// lines that aren't an entry, kept as they are.</summary>
/// <param name="Comments">The comment line just above the entry, or for lines that aren't one, the lines themselves.</param>
/// <param name="Entry">The entry's line, or null.</param>
public sealed record StoreLine(IReadOnlyList<string> Comments, string? Entry)
{
    /// <summary>What the entry is, in words: its comment without the <c>#</c>, or the entry itself.</summary>
    public string What => Comments.Count > 0 && Entry is not null ? Comments[^1].TrimStart('#', ' ') : Entry ?? "";
}

/// <summary>
/// The lines of a store file such as <c>grants</c> or <c>revoked</c>, each entry with the one comment line Meep writes
/// above it, so an entry and its comment can be taken out together and everything else is written back as it was.
/// </summary>
public sealed class StoreLines
{
    private readonly List<StoreLine> _lines;

    private StoreLines(List<StoreLine> lines) => _lines = lines;

    /// <summary>The entries, with their comments.</summary>
    public IEnumerable<StoreLine> Entries => _lines.Where(line => line.Entry is not null);

    /// <summary>Reads a file's text. Blank lines and lines starting <c>#</c> aren't entries.</summary>
    public static StoreLines Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<StoreLine>();
        var pending = new List<string>();
        var raw = text.Split('\n');
        foreach (var line in text.EndsWith('\n') ? raw[..^1] : raw)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
            {
                pending.Add(line);
                continue;
            }
            if (trimmed.Length == 0)
            {
                pending.Add(line);
                lines.Add(new StoreLine([.. pending], null));
                pending.Clear();
                continue;
            }
            if (pending.Count > 1)
                lines.Add(new StoreLine([.. pending[..^1]], null));
            lines.Add(new StoreLine(pending.Count > 0 ? [pending[^1]] : [], line));
            pending.Clear();
        }
        if (pending.Count > 0)
            lines.Add(new StoreLine([.. pending], null));
        return new StoreLines(lines);
    }

    /// <summary>
    /// Takes out each entry <paramref name="whyGoes"/> gives a reason for, with its comment, and returns what each was
    /// and why it went.
    /// </summary>
    public IReadOnlyList<string> RemoveWhere(Func<StoreLine, string?> whyGoes)
    {
        ArgumentNullException.ThrowIfNull(whyGoes);
        var removed = new List<string>();
        _lines.RemoveAll(line =>
        {
            if (line.Entry is null || whyGoes(line) is not { } why)
                return false;
            removed.Add($"{line.What}: {why}");
            return true;
        });
        return removed;
    }

    /// <summary>The file's text, each line ending in a new line.</summary>
    public override string ToString() =>
        string.Concat(_lines.SelectMany(line => line.Entry is null ? line.Comments : [.. line.Comments, line.Entry]).Select(line => line + "\n"));
}
