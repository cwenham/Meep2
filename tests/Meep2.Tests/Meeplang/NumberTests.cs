using System.Text.Json;
using Meep2.Meeplang.Compilation;
using Meep2.Meeplang.Parsing;
using Meep2.Modules;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.SDK.Numbers;
using Meep2.Tests.Support;

namespace Meep2.Tests.Meeplang;

/// <summary>Numbers in Meeplang are decimals, and carry on as big numbers when a decimal can't hold them (Q81).</summary>
public class NumberTests
{
    private static CompileScope Scope() => new(new Dictionary<string, ConstantValue>(), _ => null, _ => null, []);

    private static CompiledComputed<T> Compile<T>(string text)
    {
        var syntax = MeeplangParser.ParseExpression(text, out var diagnostics)
            ?? throw new InvalidOperationException(string.Join("; ", diagnostics));
        return new CompiledComputed<T>(syntax, Scope(), text);
    }

    // Works out an expression that doesn't use msg, as constants are.
    private static object? Value(string text) =>
        ExpressionCompiler.Evaluate(MeeplangParser.ParseExpression(text, out _)!, Scope()).Value;

    private static JsonMessage Json(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    [Test]
    public async Task Dividing_whole_numbers_gives_a_fraction()
    {
        await Assert.That(Value("7 / 2")).IsEqualTo(3.5m);
        await Assert.That(Compile<string>("\"{msg.Value / 2}\"").Evaluate(new Number(7))).IsEqualTo("3.5");
        await Assert.That(Compile<string>("\"{msg.n / 4}\"").Evaluate(Json("""{ "n": 10 }"""))).IsEqualTo("2.5");
    }

    [Test]
    public async Task Sums_are_worked_out_exactly_in_decimal()
    {
        await Assert.That(Value("0.1 + 0.2 == 0.3") is true).IsTrue();
        await Assert.That(Value("\"{1.50 * 2}\"")).IsEqualTo("3.00");
        await Assert.That(Value("1 / 3 * 3")).IsEqualTo(0.9999999999999999999999999999m);
        await Assert.That(Value("-7 % 2")).IsEqualTo(-1m);
    }

    [Test]
    public async Task Numbers_too_big_for_decimal_carry_on_as_big_numbers()
    {
        await Assert.That(Value("79228162514264337593543950335 + 1")).IsEqualTo(BigDecimal.Parse("79228162514264337593543950336"));
        await Assert.That(Value("\"{100000000000000000000 * 100000000000000000000}\"")).IsEqualTo("10000000000000000000000000000000000000000");
        await Assert.That(Value("123456789012345678901234567890123") is BigDecimal).IsTrue();
        await Assert.That(Value("-123456789012345678901234567890123")).IsEqualTo(BigDecimal.Parse("-123456789012345678901234567890123"));
    }

    [Test]
    public async Task Numbers_too_small_or_too_precise_for_decimal_keep_their_digits()
    {
        await Assert.That(Value("\"{0.00000000000001 * 0.00000000000001 * 0.001}\"")).IsEqualTo("0." + new string('0', 30) + "1");
        await Assert.That(Value("\"{0.12345678901234567890123456789012}\"")).IsEqualTo("0.12345678901234567890123456789012");
    }

    [Test]
    public async Task A_big_number_goes_back_to_being_a_decimal_when_it_fits()
    {
        await Assert.That(Value("79228162514264337593543950335") is decimal).IsTrue();

        var value = Value("79228162514264337593543950335 + 1 - 1");

        await Assert.That(value is decimal).IsTrue();
        await Assert.That(value).IsEqualTo(decimal.MaxValue);
    }

    [Test]
    public async Task A_typed_sum_that_overflows_decimal_switches_to_big_numbers()
    {
        var product = Compile<string>("\"{msg.Value * 100000000000000000000000000}\"");

        await Assert.That(product.Evaluate(new Number(7))).IsEqualTo("700000000000000000000000000");
        await Assert.That(product.Evaluate(new Number(1000))).IsEqualTo("100000000000000000000000000000");
        await Assert.That(product.Evaluate(new Number(7))).IsEqualTo("700000000000000000000000000");
    }

    [Test]
    public async Task Json_numbers_are_read_exactly()
    {
        var message = Json("""{ "id": 123456789012345678901234567890123, "tiny": 1e-30, "price": 12.50, "rate": 1.5e3 }""");

        await Assert.That(Compile<string>("\"{msg.id + 1}\"").Evaluate(message)).IsEqualTo("123456789012345678901234567890124");
        await Assert.That(Compile<bool>("msg.id > 79228162514264337593543950335").Evaluate(message)).IsTrue();
        await Assert.That(Compile<string>("\"{msg.tiny * 2}\"").Evaluate(message)).IsEqualTo("0." + new string('0', 29) + "2");
        await Assert.That(Compile<string>("\"{msg.price}\"").Evaluate(message)).IsEqualTo("12.50");
        await Assert.That(Compile<bool>("msg.rate == 1500").Evaluate(message)).IsTrue();
    }

    [Test]
    public async Task Doubles_from_csharp_types_stay_approximate()
    {
        var reading = new Reading(0.1);

        await Assert.That(Compile<object>("msg.Value * 3").Evaluate(reading)).IsEqualTo(0.1 * 3);
        await Assert.That(Compile<bool>("msg.Value < 0.2").Evaluate(reading)).IsTrue();
        await Assert.That(Compile<bool>("msg.Value > 0").Evaluate(new Reading(double.NaN))).IsFalse();
        await Assert.That(Compile<bool>("msg.Value != 0").Evaluate(new Reading(double.NaN))).IsTrue();
    }

    [Test]
    public async Task Dividing_by_zero_is_an_error()
    {
        await Assert.That(() => Compile<object>("msg.Value / 0").Evaluate(new Number(1)))
            .Throws<EvaluationException>()
            .WithMessage("Couldn't work out msg.Value / 0: Can't divide by zero.");
        await Assert.That(() => Compile<object>("msg.n % 0").Evaluate(Json("""{ "n": 1 }""")))
            .Throws<EvaluationException>()
            .WithMessage("Couldn't work out msg.n % 0: Can't divide by zero.");
    }

    [Test]
    public async Task Whole_numbers_are_needed_where_csharp_takes_them()
    {
        await Assert.That(Compile<string>("msg.Error.Substring(6)").Evaluate(new ErrorMessage { Error = "boom: reset" })).IsEqualTo("reset");
        await Assert.That(Compile<string>("msg.stops[msg.n - 1]").Evaluate(Json("""{ "n": 2, "stops": ["Hove", "Brighton"] }"""))).IsEqualTo("Brighton");
        await Assert.That(() => Compile<string>("msg.stops[msg.n / 4]").Evaluate(Json("""{ "n": 2, "stops": ["Hove", "Brighton"] }""")))
            .Throws<EvaluationException>()
            .WithMessage("Couldn't work out msg.stops[msg.n / 4]: A position has to be a whole number, but this is the number 0.5.");
    }

    [Test]
    public async Task Big_numbers_take_standard_formats_in_templates()
    {
        var message = Json("""{ "id": 123456789012345678901234567890123.456 }""");

        await Assert.That(Compile<string>("\"{msg.id:N2}\"").Evaluate(message)).IsEqualTo("123,456,789,012,345,678,901,234,567,890,123.46");
        await Assert.That(() => Compile<string>("\"{msg.id:0.00}\"").Evaluate(message))
            .Throws<EvaluationException>()
            .WithMessage("Couldn't work out \"{msg.id:0.00}\": A big number can be written with the formats G, F, N, E, P, and D for whole numbers, each with an optional number of digits, but not '0.00'.");
    }

    [Test]
    public async Task A_json_number_with_too_many_digits_is_an_error()
    {
        await Assert.That(() => Compile<string>("\"{msg.n}\"").Evaluate(Json("""{ "n": 1e5000 }""")))
            .Throws<EvaluationException>()
            .WithMessage("Couldn't work out \"{msg.n}\": A JSON number has more than 1,000 digits, which is more than Meeplang works with.");
    }
}
