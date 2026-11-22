using System.Collections.Concurrent;
using System.Text;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// The books that come with Meep, for <see cref="Enumerate"/>: common texts for testing, security audits and quick
/// jobs, without needing a file from somewhere. Each is read the first time a pipeline names it.
/// </summary>
public static class Library
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> s_books = new(StringComparer.Ordinal);

    /// <summary>Six paragraphs of Lorem Ipsum, for testing how text is shown.</summary>
    [LibraryBook("Lorem Ipsum")]
    public static IReadOnlyList<string> LoremIpsum => Read("LoremIpsum.txt");

    /// <summary>
    /// The 1,000 most common passwords, from Daniel Miessler's SecLists (MIT License), for security audits and
    /// password policies. See <c>Library/NOTICE.md</c>.
    /// </summary>
    [LibraryBook("Common Passwords")]
    public static IReadOnlyList<string> CommonPasswords => Read("CommonPasswords.txt");

    /// <summary>The 1,000 most common last names in the US, for test data and finding accounts in security audits.</summary>
    [LibraryBook("Common Last Names")]
    public static IReadOnlyList<string> CommonLastNames => Read("CommonLastNames.txt");

    /// <summary>
    /// The world's countries (Q167, Q170): the original Meep's 196 politically recognised countries, and Palestine, in
    /// alphabetical order. For territories too, see <see cref="Iso3166Countries"/>.
    /// </summary>
    [LibraryBook("Countries")]
    public static IReadOnlyList<string> Countries => Read("Countries.txt");

    /// <summary>
    /// The ISO 3166-1 list of countries and territories (249), by their English short names, such as "Bolivia,
    /// Plurinational State of", in ISO's order.
    /// </summary>
    [LibraryBook("ISO 3166 Countries")]
    public static IReadOnlyList<string> Iso3166Countries => Read("Iso3166Countries.txt");

    /// <summary>
    /// The two-letter ISO 3166-1 codes of <see cref="Iso3166Countries"/>, in the same order, so the same index gives a
    /// country's code.
    /// </summary>
    [LibraryBook("ISO 3166 Codes")]
    public static IReadOnlyList<string> Iso3166Codes => Read("Iso3166Codes.txt");

    /// <summary>The 50 US states, then the District of Columbia and the five inhabited territories.</summary>
    [LibraryBook("US States")]
    public static IReadOnlyList<string> USStates => Read("USStates.txt");

    /// <summary>The postal abbreviations of <see cref="USStates"/>, in the same order, so the same index gives a state's.</summary>
    [LibraryBook("US Abbreviations")]
    public static IReadOnlyList<string> USAbbreviations => Read("USAbbreviations.txt");

    /// <summary>Santa's eight reindeer, handy as test account names.</summary>
    [LibraryBook("Reindeer")]
    public static IReadOnlyList<string> Reindeer => Read("Reindeer.txt");

    /// <summary>The names of the text encodings this machine's .NET knows, such as <c>utf-8</c>.</summary>
    [LibraryBook("Encodings")]
    public static IReadOnlyList<string> Encodings =>
        s_books.GetOrAdd("Encodings", _ => [.. Encoding.GetEncodings().Select(e => e.Name)]);

    private static IReadOnlyList<string> Read(string file) => s_books.GetOrAdd(file, static name =>
    {
        using var stream = typeof(Library).Assembly.GetManifestResourceStream($"Meep2.Modules.Library.{name}")
            ?? throw new InvalidOperationException($"The book {name} is missing from Meep2.Modules.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return [.. reader.ReadToEnd().Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0)];
    });
}
