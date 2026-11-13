using Meep2.Meeplang.Loading;
using Meep2.Modules;
using Meep2.SDK.Messages;
using Meep2.Tests.Support;

namespace Meep2.Tests.Meeplang;

/// <summary>Enumerate, and the library of books it reads from.</summary>
public sealed class EnumerateTests
{
    private static async Task<List<BookItem>> Items(string pipeline)
    {
        var run = await TestRun.Start(pipeline);
        await Assert.That(run.Diagnostics).IsEqualTo("");
        return [.. run.At("Sink").Cast<BookItem>()];
    }

    [Test]
    public async Task Without_an_index_it_steps_through_the_book_in_order_and_wraps_around()
    {
        var items = await Items("""
            Interval 1ms count=10
                Enumerate "Reindeer"
                    Sink
            """);

        await Assert.That(items.Select(i => i.Text)).IsEquivalentTo(
            ["Dasher", "Dancer", "Prancer", "Vixen", "Comet", "Cupid", "Donner", "Blitzen", "Dasher", "Dancer"]);
        await Assert.That(items.Select(i => i.Index)).IsEquivalentTo([0L, 1, 2, 3, 4, 5, 6, 7, 0, 1]);
        await Assert.That(items[0].Book).IsEqualTo("Reindeer");
    }

    [Test]
    public async Task An_index_chooses_the_item_counting_from_zero_and_wrapping_both_ways()
    {
        // -1, 4 and 9 in a book of 8.
        var items = await Items("""
            Interval 1ms count=3
                Enumerate "Reindeer" index=msg.Count * 5 - 6
                    Sink
            """);

        await Assert.That(items.Select(i => i.Text)).IsEquivalentTo(["Blitzen", "Comet", "Dancer"]);
        await Assert.That(items.Select(i => i.Index)).IsEquivalentTo([7L, 4, 1]);
    }

    [Test]
    public async Task A_misspelt_book_is_reported_when_the_pipeline_loads()
    {
        var run = await TestRun.Start("""
            Interval 10ms count=1
                Enumerate "Countrys"
            """, run: false);

        await Assert.That(run.Diagnostics).Contains("There's no book called Countrys in the library. Did you mean 'Countries'?");
    }

    [Test]
    public async Task The_book_has_to_be_the_same_for_every_message()
    {
        var run = await TestRun.Start("""
            Interval 10ms count=1 payload="Countries"
                Enumerate msg.Payload
            """, run: false);

        await Assert.That(run.Diagnostics).Contains("book needs the name of a book from the library, such as \"Countries\".");
    }

    [Test]
    public async Task Errors_are_passed_on_untouched()
    {
        var run = await TestRun.Start("""
            Interval 10ms count=1
                Boom
                    Enumerate "Reindeer"
                        Sink
            """);

        await Assert.That(run.At("Sink").Single()).IsTypeOf<ErrorMessage>();
    }

    [Test]
    public async Task Every_built_in_book_loads()
    {
        var registry = new ModuleRegistry().AddAssembly(typeof(Enumerate).Assembly);
        string[] names =
        [
            "Lorem Ipsum", "Common Passwords", "Common Last Names", "Countries", "ISO 3166 Countries", "ISO 3166 Codes",
            "US States", "US Abbreviations", "Reindeer", "Encodings",
        ];

        await Assert.That(registry.Books).IsEquivalentTo(names);
        foreach (var name in names)
        {
            await Assert.That(registry.TryGetBook(name, out var book)).IsTrue();
            await Assert.That(book!.Items.Count).IsGreaterThan(0);
            await Assert.That(book.Items.All(item => item.Length > 0 && item.Trim() == item)).IsTrue();
        }

        await Assert.That(Book(registry, "Common Passwords").Count).IsEqualTo(1000);
        await Assert.That(Book(registry, "Lorem Ipsum").Count).IsEqualTo(6);
        await Assert.That(Book(registry, "Countries").Count).IsEqualTo(197);
        await Assert.That(Book(registry, "Countries")).Contains("Palestine");
        await Assert.That(Book(registry, "Countries")).Contains("Taiwan");

        var states = Book(registry, "US States");
        var abbreviations = Book(registry, "US Abbreviations");
        await Assert.That(abbreviations.Count).IsEqualTo(states.Count);
        await Assert.That(abbreviations[states.ToList().IndexOf("New York")]).IsEqualTo("NY");

        var countries = Book(registry, "ISO 3166 Countries");
        var codes = Book(registry, "ISO 3166 Codes");
        await Assert.That(countries.Count).IsEqualTo(249);
        await Assert.That(codes.Count).IsEqualTo(249);
        await Assert.That(codes.Distinct().Count()).IsEqualTo(249);
        await Assert.That(codes.All(code => code.Length == 2 && code.All(char.IsAsciiLetterUpper))).IsTrue();
        await Assert.That(codes[countries.ToList().IndexOf("United Kingdom of Great Britain and Northern Ireland")]).IsEqualTo("GB");
        await Assert.That(codes[countries.ToList().IndexOf("Åland Islands")]).IsEqualTo("AX");
    }

    private static IReadOnlyList<string> Book(ModuleRegistry registry, string name) =>
        registry.TryGetBook(name, out var book) ? book.Items : throw new InvalidOperationException($"No book {name}.");

    [Test]
    public async Task Book_names_are_matched_ignoring_case_and_items_carry_the_librarys_spelling()
    {
        var items = await Items("""
            Interval 1ms count=1
                Enumerate "common PASSWORDS"
                    Sink
            """);

        await Assert.That(items.Single().Book).IsEqualTo("Common Passwords");
        await Assert.That(items.Single().Text).IsEqualTo("123456");
    }

    [Test]
    public async Task All_brings_out_the_whole_book_for_each_message()
    {
        var items = await Items("""
            Interval 1ms count=2
                Enumerate "Reindeer" all=true
                    Sink
            """);

        await Assert.That(items.Count).IsEqualTo(16);
        await Assert.That(items.Take(8).Select(i => i.Index)).IsEquivalentTo([0L, 1, 2, 3, 4, 5, 6, 7]);
        await Assert.That(items.Select(i => i.Text).Distinct().Count()).IsEqualTo(8);
    }

    [Test]
    public async Task All_and_index_together_are_reported_when_the_pipeline_loads()
    {
        var run = await TestRun.Start("""
            Interval 10ms count=1
                Enumerate "Reindeer" all=true index=3
            """, run: false);

        await Assert.That(run.Diagnostics).Contains("takes index or all=true, not both");
    }

    [Test]
    public async Task All_warns_when_the_book_is_larger_than_a_buffer_after_it()
    {
        var small = await TestRun.Start("""
            Interval 10ms count=1
                Enumerate "Common Passwords" all=true
                    WriteLine @buffer=10
                        Sink @buffer=500
            """, run: false);
        var raised = await TestRun.Start("""
            Interval 10ms count=1
                Enumerate "Common Passwords" all=true
                    Sink @buffer=1000
            """, run: false);
        var one = await TestRun.Start("""
            Interval 10ms count=1
                Enumerate "Common Passwords"
                    Sink @buffer=1
            """, run: false);

        await Assert.That(small.Diagnostics).Contains("Enumerate can emit 1,000 messages at once, but WriteLine only holds 10, so the earliest would be dropped. Give WriteLine @buffer=1000, or raise buffer under defaults.");
        await Assert.That(small.Diagnostics).Contains("but Sink only holds 500");
        await Assert.That(raised.Diagnostics).IsEqualTo("");
        await Assert.That(one.Diagnostics).IsEqualTo("");
    }

    [Test]
    public async Task The_message_that_asked_for_an_item_is_its_parent()
    {
        var items = await Items("""
            Interval 1ms count=1 count-start=40
                Enumerate "Reindeer"
                    Sink
            """);

        await Assert.That(items.Single().Parents.Single()).IsTypeOf<Tick>();
        await Assert.That(((Tick)items.Single().Parents.Single()).Count).IsEqualTo(40L);
    }

    [Test]
    public async Task Interval_numbers_its_ticks_from_count_start()
    {
        var run = await TestRun.Start("""
            Interval 1ms count=3 count-start=0
                Enumerate "Reindeer" index=msg.Count
                    Sink
            """);

        await Assert.That(run.Diagnostics).IsEqualTo("");
        await Assert.That(run.At("Sink").Cast<BookItem>().Select(i => i.Text)).IsEquivalentTo(["Dasher", "Dancer", "Prancer"]);
    }
}
