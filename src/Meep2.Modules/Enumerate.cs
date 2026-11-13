using System.Collections.Concurrent;
using System.Text;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Modules;

/// <summary>
/// Emits an item from a book in the library for each message it receives, as a <see cref="BookItem"/>. In Meeplang:
/// <c>Enumerate "Countries"</c> to step through a book in order, one item for each message, or
/// <c>Enumerate "Lorem Ipsum" index=msg.Count</c> to choose the item, or <c>Enumerate "Common Passwords" all=true</c>
/// for every item.
/// </summary>
/// <remarks>
/// <para>
/// The index counts from 0 and wraps around the end of the book, so 12 in a book of 5 items is item 2, and -1 is the
/// last. Without <c>index</c>, Enumerate keeps its own place in the book: it starts at item 0 and moves on one each
/// time, wrapping at the end (Q162). The message that asked for the item is its parent, so <c>fam</c> reaches it. To
/// pace it, put it under an Interval.
/// </para>
/// <para>
/// With <c>all=true</c>, each message brings out the whole book, in order (Q164). Emitting never waits, so a book larger
/// than the next module's buffer would lose its first items. The loader warns about that (Q169); raise <c>buffer</c>
/// for a book that large.
/// </para>
/// <para>
/// The built-in books are in <see cref="Library"/>. Plugins add more with <see cref="LibraryBookAttribute"/>. Errors
/// are passed on untouched.
/// </para>
/// </remarks>
[Verb("Enumerate")]
[Emits(typeof(BookItem))]
public sealed class Enumerate(EnumerateParameters parameters)
    : AInflection<Message>, IChosenByParameters<EnumerateParameters>, IEmitsAtOnce<EnumerateParameters>
{
    private long _next;

    /// <summary>Takes <c>index</c> or <c>all</c>, but not both.</summary>
    public static string? WhyNot(EnumerateParameters parameters, Message message)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.All && parameters.Index is not null ? "takes index or all=true, not both" : null;
    }

    /// <summary>With <c>all=true</c>, the whole book.</summary>
    public static long? MostAtOnce(EnumerateParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.All ? parameters.Book.Items.Count : null;
    }

    /// <inheritdoc/>
    protected override ValueTask HandleAsync(Message message, ModuleContext context, CancellationToken cancellationToken)
    {
        if (message is ErrorMessage)
        {
            context.Emit(message);
            return ValueTask.CompletedTask;
        }

        var items = parameters.Book.Items;
        if (parameters.All)
        {
            for (var i = 0; i < items.Count; i++)
                context.Emit(new BookItem(parameters.Book.Name, i, items[i]));
            return ValueTask.CompletedTask;
        }

        long index;
        try
        {
            index = parameters.Index is { } chosen ? chosen.Evaluate(message) : _next++;
        }
        catch (EvaluationException exception)
        {
            context.EmitError(exception.Message, exception);
            return ValueTask.CompletedTask;
        }

        var place = ((index % items.Count) + items.Count) % items.Count;
        context.Emit(new BookItem(parameters.Book.Name, place, items[(int)place]));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Parameters for <see cref="Enumerate"/>.</summary>
public sealed record EnumerateParameters
{
    /// <summary>The book to read from, such as <c>"Countries"</c>, or <c>"Plugin.Book"</c> for a plugin's.</summary>
    [Positional]
    public required Book Book { get; init; }

    /// <summary>
    /// Which item to emit, worked out for each message, counting from 0 and wrapping around the end. Without it, the
    /// items in order, one for each message.
    /// </summary>
    public Computed<long>? Index { get; init; }

    /// <summary>Emits every item in the book, in order, for each message, instead of one.</summary>
    public bool All { get; init; }
}

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
