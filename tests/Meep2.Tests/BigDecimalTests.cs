using System.Globalization;
using System.Numerics;
using Meep2.SDK.Numbers;

namespace Meep2.Tests;

public class BigDecimalTests
{
    private static BigDecimal Big(string text) => BigDecimal.Parse(text, CultureInfo.InvariantCulture);

    [Test]
    [Arguments("0", "0")]
    [Arguments("-12.50", "-12.50")]
    [Arguments("1.5e3", "1500")]
    [Arguments("1E-30", "0.000000000000000000000000000001")]
    [Arguments("000123.4500", "123.4500")]
    [Arguments(".5", "0.5")]
    [Arguments("123456789012345678901234567890123", "123456789012345678901234567890123")]
    public async Task Numbers_are_read_and_written_in_full(string text, string written)
    {
        await Assert.That(Big(text).ToString()).IsEqualTo(written);
    }

    [Test]
    [Arguments("")]
    [Arguments("abc")]
    [Arguments("1.2.3")]
    [Arguments("1e")]
    [Arguments("--1")]
    [Arguments("1,000")]
    public async Task Text_that_isnt_a_number_is_rejected(string text)
    {
        await Assert.That(BigDecimal.TryParse(text, CultureInfo.InvariantCulture, out _)).IsFalse();
    }

    [Test]
    public async Task Adding_subtracting_and_multiplying_keep_every_digit()
    {
        var largestDecimal = (BigDecimal)decimal.MaxValue;
        var squared = BigInteger.Parse("79228162514264337593543950335", CultureInfo.InvariantCulture);
        squared *= squared;

        await Assert.That((largestDecimal + 1).ToString()).IsEqualTo("79228162514264337593543950336");
        await Assert.That((largestDecimal * largestDecimal).ToString()).IsEqualTo(squared.ToString(CultureInfo.InvariantCulture));
        await Assert.That((-largestDecimal - largestDecimal).ToString()).IsEqualTo("-158456325028528675187087900670");
        await Assert.That((Big("0.1") + Big("0.2")).ToString()).IsEqualTo("0.3");
        await Assert.That((Big("1.50") * 2).ToString()).IsEqualTo("3.00");
        await Assert.That((Big("1e-20") * Big("1e-20")).ToString()).IsEqualTo("0." + new string('0', 39) + "1");
    }

    [Test]
    [Arguments("7", "2", "3.5")]
    [Arguments("6.0", "2", "3.0")]
    [Arguments("1.00", "4", "0.25")]
    [Arguments("100", "4", "25")]
    [Arguments("1", "8", "0.125")]
    [Arguments("-7", "2", "-3.5")]
    [Arguments("1", "3", "0.3333333333333333333333333333")]
    [Arguments("2", "3", "0.6666666666666666666666666667")]
    public async Task Division_gives_the_same_answers_as_decimal(string left, string right, string expected)
    {
        var asDecimal = decimal.Parse(left, CultureInfo.InvariantCulture) / decimal.Parse(right, CultureInfo.InvariantCulture);

        await Assert.That(asDecimal.ToString(CultureInfo.InvariantCulture)).IsEqualTo(expected);
        await Assert.That((Big(left) / Big(right)).ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task Division_keeps_28_significant_digits_or_as_many_as_the_numbers_have()
    {
        var thirty = Big("123456789012345678901234567890");

        await Assert.That((Big("1e40") / 3).ToString()).IsEqualTo(new string('3', 28) + new string('0', 12));
        await Assert.That((thirty / 1).ToString()).IsEqualTo("123456789012345678901234567890");
        await Assert.That((thirty * 2 / 2).ToString()).IsEqualTo("123456789012345678901234567890");
        await Assert.That(BigDecimal.Divide(1, 3, 5).ToString()).IsEqualTo("0.33333");
        await Assert.That(() => Big("1") / 0).Throws<DivideByZeroException>();
    }

    [Test]
    public async Task A_remainder_has_the_dividends_sign_as_for_decimal()
    {
        await Assert.That((Big("7.5") % 2).ToString()).IsEqualTo((7.5m % 2).ToString(CultureInfo.InvariantCulture));
        await Assert.That((Big("-7.5") % 2).ToString()).IsEqualTo((-7.5m % 2).ToString(CultureInfo.InvariantCulture));
        await Assert.That((Big("1e30") % 7).ToString()).IsEqualTo((BigInteger.Pow(10, 30) % 7).ToString(CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task Numbers_that_differ_only_in_trailing_zeros_are_equal()
    {
        await Assert.That(Big("1.0") == Big("1.00")).IsTrue();
        await Assert.That(Big("1.0").GetHashCode()).IsEqualTo(Big("1").GetHashCode());
        await Assert.That(Big("1500").GetHashCode()).IsEqualTo(Big("1.5e3").GetHashCode());
        await Assert.That(Big("-1") < 0).IsTrue();
        await Assert.That(Big("0.5") < 1).IsTrue();
        await Assert.That(Big("1e30") > Big("999999999999999999999999999999.9")).IsTrue();
        await Assert.That(Big("0") == Big("-0.000")).IsTrue();
    }

    [Test]
    public async Task A_decimal_comes_out_only_when_one_holds_the_number_exactly()
    {
        await Assert.That(((BigDecimal)decimal.MaxValue).TryGetDecimal(out var largest) && largest == decimal.MaxValue).IsTrue();
        await Assert.That(((BigDecimal)decimal.MinValue).TryGetDecimal(out var smallest) && smallest == decimal.MinValue).IsTrue();
        await Assert.That(((BigDecimal)decimal.MaxValue + 1).TryGetDecimal(out _)).IsFalse();
        await Assert.That(Big("1e-28").TryGetDecimal(out var tiny) && tiny == 0.0000000000000000000000000001m).IsTrue();
        await Assert.That(Big("1.000e-26").TryGetDecimal(out _)).IsTrue();
        await Assert.That(Big("1e-29").TryGetDecimal(out _)).IsFalse();
        await Assert.That(Big("1.50").TryGetDecimal(out var price) && price.ToString(CultureInfo.InvariantCulture) == "1.50").IsTrue();
    }

    [Test]
    public async Task Conversions_round_or_refuse_as_their_types_need()
    {
        await Assert.That((decimal)Big("0.12345678901234567890123456789012")).IsEqualTo(0.1234567890123456789012345679m);
        await Assert.That(() => (decimal)Big("1e30")).Throws<OverflowException>();
        await Assert.That((double)Big("0.1")).IsEqualTo(0.1);
        await Assert.That(double.IsPositiveInfinity((double)Big("1e400"))).IsTrue();
        await Assert.That((BigInteger)Big("-7.9")).IsEqualTo(new BigInteger(-7));
        await Assert.That(BigDecimal.IsInteger(Big("1.5e3"))).IsTrue();
        await Assert.That(BigDecimal.IsInteger(Big("1.5"))).IsFalse();
    }

    [Test]
    public async Task Standard_formats_match_decimals()
    {
        decimal[] values = [1234567.891m, -1234.5m, 0m, 2.5m, 0.125m, decimal.MaxValue, 0.0000000000000000000000000001m];
        string[] formats = ["F2", "F0", "N2", "N0", "E3", "e1", "P1", "G", "N"];

        var differences = new List<string>();
        foreach (var value in values)
        {
            foreach (var format in formats)
            {
                var expected = value.ToString(format, CultureInfo.InvariantCulture);
                var actual = ((BigDecimal)value).ToString(format, CultureInfo.InvariantCulture);
                if (actual != expected)
                    differences.Add($"{value} with {format}: expected {expected}, got {actual}");
            }
        }

        await Assert.That(string.Join("\n", differences)).IsEqualTo("");
    }

    [Test]
    public async Task Big_numbers_take_standard_formats_and_refuse_custom_ones()
    {
        var big = Big("123456789012345678901234567890123.456");

        await Assert.That(big.ToString("N2", CultureInfo.InvariantCulture)).IsEqualTo("123,456,789,012,345,678,901,234,567,890,123.46");
        await Assert.That(big.ToString("E3", CultureInfo.InvariantCulture)).IsEqualTo("1.235E+032");
        await Assert.That(Big("1e30").ToString("D", CultureInfo.InvariantCulture)).IsEqualTo("1" + new string('0', 30));
        await Assert.That(() => big.ToString("D", CultureInfo.InvariantCulture)).Throws<FormatException>();
        await Assert.That(() => big.ToString("0.00", CultureInfo.InvariantCulture)).Throws<FormatException>();
    }

    [Test]
    public async Task Written_out_a_number_has_at_most_1000_digits()
    {
        await Assert.That(Big("1e999").ToString().Length).IsEqualTo(1000);
        await Assert.That(Big("-1e-999").ToString()).IsEqualTo("-0." + new string('0', 998) + "1");
        await Assert.That(() => Big("1e1000")).Throws<OverflowException>();
        await Assert.That(() => Big("1e-1000")).Throws<OverflowException>();
        await Assert.That(() => Big("1e999999999")).Throws<OverflowException>();
        await Assert.That(() => Big("1e999") * 10).Throws<OverflowException>();
        await Assert.That(() => Big("1e999") + Big("1e-999")).Throws<OverflowException>();
        await Assert.That(Big("0e999999999").IsZero).IsTrue();
    }
}
