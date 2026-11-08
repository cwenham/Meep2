using Meep2.SDK.Messages;
using Meep2.Tests.Support;

namespace Meep2.Tests;

public class MessageTests
{
    [Test]
    public async Task A_new_message_has_an_id_a_creation_time_and_no_parents()
    {
        var before = DateTime.UtcNow;
        var message = new Number(1);

        await Assert.That(message.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(message.Created).IsGreaterThanOrEqualTo(before);
        await Assert.That(message.Created.Kind).IsEqualTo(DateTimeKind.Utc);
        await Assert.That(message.Parents.Count).IsEqualTo(0);
        await Assert.That(message.EmittedBy).IsNull();
    }

    [Test]
    public async Task Ids_are_uuid_version_7_and_sort_by_creation_time()
    {
        var earlier = new Number(1);
        await Task.Delay(5);
        var later = new Number(2);

        await Assert.That(earlier.Id.Version).IsEqualTo(7);
        await Assert.That(earlier.Id.Variant >> 2).IsEqualTo(0b10); // RFC 9562 variant: the top two bits are 10
        await Assert.That(earlier.Id.CompareTo(later.Id)).IsLessThan(0);
        await Assert.That(DateTimeOffset.FromUnixTimeMilliseconds(UnixMilliseconds(earlier.Id)).UtcDateTime)
            .IsEqualTo(earlier.Created.AddTicks(-(earlier.Created.Ticks % TimeSpan.TicksPerMillisecond)));
    }

    [Test]
    public async Task Ids_made_on_many_threads_at_once_are_all_different()
    {
        var ids = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 25_000; i++)
                ids.Add(new Number(i).Id);
        });

        await Assert.That(ids.Distinct().Count()).IsEqualTo(200_000);
    }

    private static long UnixMilliseconds(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        return (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes) >> 16;
    }

    [Test]
    public async Task A_copy_made_with_with_is_a_new_child_of_the_original()
    {
        var original = new Number(1);
        var copy = original with { Value = 2 };

        await Assert.That(copy.Parent).IsSameReferenceAs(original);
        await Assert.That(copy.Parents.Count).IsEqualTo(1);
        await Assert.That(copy.Id).IsNotEqualTo(original.Id);
        await Assert.That(copy.Value).IsEqualTo(2);
    }

    [Test]
    public async Task A_copy_keeps_the_originals_expiry()
    {
        var deadline = Deadline.After(TimeSpan.FromMinutes(5));
        var copy = new Number(1) { Expires = deadline } with { Value = 2 };

        await Assert.That(copy.Expires).IsEqualTo(deadline);
    }

    [Test]
    public async Task Messages_are_only_equal_to_themselves()
    {
        var first = new Number(1);
        var second = new Number(1);

        await Assert.That(first.Equals(second)).IsFalse();
        await Assert.That(first.Equals(first)).IsTrue();
        await Assert.That(first == (first with { })).IsFalse();
    }

    [Test]
    public async Task ToString_shows_the_message_but_not_its_ancestors()
    {
        Message message = new Word("root");
        for (var i = 0; i < 50; i++)
            message = new Word($"child {i}") { Parents = new Parents(message) };

        var text = message.ToString();

        await Assert.That(text).Contains("child 49");
        await Assert.That(text).DoesNotContain("root");
    }

    [Test]
    public async Task Parents_hold_none_one_or_many_in_order()
    {
        var first = new Number(1);
        var second = new Number(2);
        Parents none = [];
        Parents one = [first];
        Parents two = [first, second];

        await Assert.That(none.Count).IsEqualTo(0);
        await Assert.That(none.First).IsNull();
        await Assert.That(one.Count).IsEqualTo(1);
        await Assert.That(one[0]).IsSameReferenceAs(first);
        await Assert.That(two.Count).IsEqualTo(2);
        await Assert.That(two.First).IsSameReferenceAs(first);
        await Assert.That(two[1]).IsSameReferenceAs(second);
        await Assert.That(Wait.Join(two.Select(p => ((Number)p).Value))).IsEqualTo("1,2");
    }

    [Test]
    public async Task The_earliest_deadline_wins()
    {
        var soon = Deadline.After(TimeSpan.FromSeconds(1));
        var later = Deadline.After(TimeSpan.FromHours(1));

        await Assert.That(Deadline.Earliest(soon, later)).IsEqualTo(soon);
        await Assert.That(Deadline.Earliest(later, soon)).IsEqualTo(soon);
        await Assert.That(Deadline.Earliest(null, later)).IsEqualTo(later);
        await Assert.That(Deadline.Earliest(null, null)).IsNull();
    }

    [Test]
    public async Task A_deadline_knows_whether_it_has_passed()
    {
        var past = Deadline.After(TimeSpan.FromSeconds(-1));
        var future = Deadline.After(TimeSpan.FromHours(1));

        await Assert.That(past.HasPassed).IsTrue();
        await Assert.That(past.Remaining).IsEqualTo(TimeSpan.Zero);
        await Assert.That(future.HasPassed).IsFalse();
        await Assert.That(future.Remaining).IsGreaterThan(TimeSpan.FromMinutes(59));
    }
}
