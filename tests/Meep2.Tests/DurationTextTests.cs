using Meep2.Meeplang.Parsing;
using Meep2.SDK.Modules;

namespace Meep2.Tests;

public class DurationTextTests
{
    [Test]
    public async Task Durations_are_written_as_meeplang_reads_them()
    {
        await Assert.That(DurationText.Write(TimeSpan.Zero)).IsEqualTo("0s");
        await Assert.That(DurationText.Write(TimeSpan.FromMilliseconds(250))).IsEqualTo("250ms");
        await Assert.That(DurationText.Write(TimeSpan.FromSeconds(100))).IsEqualTo("1m40s");
        await Assert.That(DurationText.Write(TimeSpan.FromMinutes(90))).IsEqualTo("1h30m");
        await Assert.That(DurationText.Write(TimeSpan.FromDays(2) + TimeSpan.FromSeconds(1))).IsEqualTo("2d1s");
        await Assert.That(DurationText.Write(TimeSpan.FromSeconds(-5))).IsEqualTo("-5s");
    }

    [Test]
    public async Task A_part_of_a_millisecond_is_written_in_the_largest_unit_that_fits()
    {
        await Assert.That(DurationText.Write(TimeSpan.FromTicks(15_000))).IsEqualTo("1.5ms");
        await Assert.That(DurationText.Write(TimeSpan.FromTicks(22_500_005))).IsEqualTo("2.25s");
        await Assert.That(DurationText.Write(TimeSpan.FromTicks(5_000))).IsEqualTo("0.5ms");
    }

    [Test]
    public async Task What_is_written_reads_back_the_same()
    {
        foreach (var duration in new[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(100), TimeSpan.FromHours(25) })
        {
            await Assert.That(Durations.TryParse(DurationText.Write(duration), out var read)).IsTrue();
            await Assert.That(read).IsEqualTo(duration);
        }
    }
}
