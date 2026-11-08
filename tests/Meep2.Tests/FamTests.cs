using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.Tests.Support;

namespace Meep2.Tests;

public class FamTests
{
    private static int s_nextNumber;

    private static ModuleInfo Module(string verb, string? name = null) =>
        new(Interlocked.Increment(ref s_nextNumber), verb, name);

    private static T EmittedBy<T>(ModuleInfo module, T message)
        where T : Message
    {
        message.EmittedBy = module;
        return message;
    }

    [Test]
    public async Task The_nearest_ancestor_wins_and_the_message_itself_is_left_out()
    {
        var numbers = Module("Numbers");
        var map = Module("Map");
        var first = EmittedBy(numbers, new Number(1));
        var second = EmittedBy(map, first with { Value = 2 });
        var third = EmittedBy(map, second with { Value = 3 });

        await Assert.That(third.Fam["Map"]).IsSameReferenceAs(second);
        await Assert.That(third.Fam["Numbers"]).IsSameReferenceAs(first);
        await Assert.That(third.Fam["Missing"]).IsNull();
        await Assert.That(first.Fam["Numbers"]).IsNull();
    }

    [Test]
    public async Task A_named_module_is_found_by_its_name_not_its_verb()
    {
        var schedule = Module("Fetch", "Schedule");
        var extract = Module("Extract");
        var timetable = EmittedBy(schedule, new Word("timetable"));
        var departure = EmittedBy(extract, timetable with { Value = "departure" });

        await Assert.That(departure.Fam["Schedule"]).IsSameReferenceAs(timetable);
        await Assert.That(departure.Fam["Fetch"]).IsNull();
    }

    [Test]
    public async Task Several_parents_are_searched_one_generation_at_a_time()
    {
        var origin = Module("Origin");
        var step = Module("Step");
        var zip = Module("Zip");
        var farOrigin = EmittedBy(origin, new Number(1));
        var left = EmittedBy(step, farOrigin with { Value = 2 });
        var nearOrigin = EmittedBy(origin, new Number(3));

        // farOrigin is two generations up through the first parent; nearOrigin is one up through the second.
        var joined = EmittedBy(zip, new Word("joined") { Parents = [left, nearOrigin] });

        await Assert.That(joined.Fam["Origin"]).IsSameReferenceAs(nearOrigin);
    }

    [Test]
    public async Task Between_equally_near_matches_the_earlier_parent_wins()
    {
        var origin = Module("Origin");
        var zip = Module("Zip");
        var first = EmittedBy(origin, new Number(1));
        var second = EmittedBy(origin, new Number(2));
        var joined = EmittedBy(zip, new Word("joined") { Parents = [first, second] });

        await Assert.That(joined.Fam["Origin"]).IsSameReferenceAs(first);
    }

    [Test]
    public async Task All_lists_every_match_nearest_first_and_each_once()
    {
        var origin = Module("Origin");
        var step = Module("Step");
        var zip = Module("Zip");
        var root = EmittedBy(origin, new Number(0));
        var left = EmittedBy(step, root with { Value = 1 });
        var right = EmittedBy(step, root with { Value = 2 });
        var joined = EmittedBy(zip, new Word("joined") { Parents = [left, right] });

        await Assert.That(Wait.Join(joined.Fam.All("Step").Select(m => ((Number)m).Value))).IsEqualTo("1,2");
        await Assert.That(joined.Fam.All("Origin").Count).IsEqualTo(1);
    }
}
