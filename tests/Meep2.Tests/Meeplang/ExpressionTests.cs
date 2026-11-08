using System.Text.Json;
using Meep2.Meeplang.Compilation;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Modules;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;
using Meep2.Tests.Support;

namespace Meep2.Tests.Meeplang;

public class ExpressionTests
{
    private static readonly Dictionary<string, Type> s_types = new()
    {
        ["Number"] = typeof(Number),
        ["Word"] = typeof(Word),
        ["ErrorMessage"] = typeof(ErrorMessage),
    };

    private static CompileScope Scope(Dictionary<string, ConstantValue>? constants = null, Func<string, FamTarget?>? fam = null) =>
        new(constants ?? [], fam ?? (_ => null), name => s_types.GetValueOrDefault(name), ["Source"]);

    private static CompiledComputed<T> Compile<T>(string text, CompileScope? scope = null)
    {
        var syntax = MeeplangParser.ParseExpression(text, out var diagnostics)
            ?? throw new InvalidOperationException(string.Join("; ", diagnostics));
        return new CompiledComputed<T>(syntax, scope ?? Scope(), text);
    }

    private static JsonMessage Json(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    [Test]
    public async Task Typed_members_and_comparisons_compile_against_the_message_type()
    {
        var below = Compile<bool>("msg.Value < 30");

        await Assert.That(below.Evaluate(new Number(7))).IsTrue();
        await Assert.That(below.Evaluate(new Number(70))).IsFalse();
    }

    [Test]
    public async Task Arithmetic_promotes_numbers_and_plus_joins_text()
    {
        await Assert.That(Compile<double>("msg.Value / 2.0 + 1").Evaluate(new Number(7))).IsEqualTo(4.5);
        await Assert.That(Compile<int>("msg.Value * 2 % 5").Evaluate(new Number(4))).IsEqualTo(3);
        await Assert.That(Compile<string>("\"n=\" + msg.Value").Evaluate(new Number(4))).IsEqualTo("n=4");
    }

    [Test]
    public async Task Templates_format_values_with_dotnet_formats()
    {
        var message = new Number(7) { Created = new DateTime(2026, 11, 8, 9, 30, 0, DateTimeKind.Utc) };

        var text = Compile<string>("\"{msg.Value:000} on {msg.Created:yyyy-MM-dd}\"").Evaluate(message);

        await Assert.That(text).IsEqualTo("007 on 2026-11-08");
    }

    [Test]
    public async Task Logic_and_type_tests_work()
    {
        var condition = Compile<bool>("msg is Number and not (msg.Value == 3 or msg.Value > 10)");

        await Assert.That(condition.Evaluate(new Number(2))).IsTrue();
        await Assert.That(condition.Evaluate(new Number(3))).IsFalse();
        await Assert.That(Compile<bool>("msg is not Word").Evaluate(new Number(1))).IsTrue();
    }

    [Test]
    public async Task Methods_can_be_called_on_typed_values()
    {
        var error = new ErrorMessage { Error = "boom: connection reset" };

        await Assert.That(Compile<bool>("msg.Error.Contains(\"reset\")").Evaluate(error)).IsTrue();
        await Assert.That(Compile<string>("msg.Error.ToUpperInvariant()").Evaluate(error)).IsEqualTo("BOOM: CONNECTION RESET");
    }

    [Test]
    public async Task Constants_are_built_into_expressions()
    {
        var scope = Scope(new Dictionary<string, ConstantValue> { ["Limit"] = new(10, typeof(int)), ["Api"] = new("https://x", typeof(string)) });

        await Assert.That(Compile<bool>("msg.Value > Limit", scope).Evaluate(new Number(11))).IsTrue();
        await Assert.That(Compile<string>("\"{Api}/{msg.Value}\"", scope).Evaluate(new Number(5))).IsEqualTo("https://x/5");
    }

    [Test]
    public async Task A_misspelled_member_doesnt_apply_and_suggests_the_right_one()
    {
        var expression = Compile<bool>("msg.Valeu < 30");

        await Assert.That(expression.AppliesTo(typeof(Number))).IsFalse();
        await Assert.That(expression.ProblemWith(typeof(Number))!.Value.Problem).IsEqualTo("Number has no 'Valeu'. Did you mean 'Value'?");
    }

    [Test]
    public async Task Comparing_a_number_with_text_is_caught_before_running()
    {
        var expression = Compile<bool>("msg.Value == \"30\"");

        await Assert.That(expression.ProblemWith(typeof(Number))!.Value.Problem).IsEqualTo("Can't compare a whole number with text.");
    }

    [Test]
    public async Task An_expression_only_applies_to_types_it_fits()
    {
        var expression = Compile<bool>("msg.Value < 3");

        await Assert.That(expression.AppliesTo(typeof(Number))).IsTrue();
        await Assert.That(expression.AppliesTo(typeof(Word))).IsFalse();
        await Assert.That(expression.AppliesTo(typeof(ErrorMessage))).IsFalse();
    }

    [Test]
    public async Task Json_members_are_read_when_each_message_arrives()
    {
        var departure = Json("""{ "route": { "name": "7" }, "minutesAway": 12, "stops": ["Hove", "Brighton"], "late": true }""");

        await Assert.That(Compile<bool>("msg.minutesAway < 30").Evaluate(departure)).IsTrue();
        await Assert.That(Compile<string>("\"{msg.route.name} in {msg.minutesAway} min\"").Evaluate(departure)).IsEqualTo("7 in 12 min");
        await Assert.That(Compile<string>("msg.stops[1]").Evaluate(departure)).IsEqualTo("Brighton");
        await Assert.That(Compile<bool>("msg.late and msg.route.name == \"7\"").Evaluate(departure)).IsTrue();
        await Assert.That(Compile<double>("msg.minutesAway * 1.5").Evaluate(departure)).IsEqualTo(18.0);
    }

    [Test]
    public async Task A_missing_json_member_fails_when_the_message_arrives()
    {
        var expression = Compile<bool>("msg.minutesAway < 30");

        await Assert.That(expression.AppliesTo(typeof(JsonMessage))).IsTrue();
        await Assert.That(() => expression.Evaluate(Json("""{ "route": "7" }""")))
            .Throws<EvaluationException>()
            .WithMessage("Couldn't work out msg.minutesAway < 30: There's no 'minutesAway' in this JsonMessage.");
    }

    [Test]
    public async Task Fam_finds_an_ancestor_and_knows_its_type()
    {
        var scope = Scope(fam: name => name == "Source" ? new FamTarget(typeof(Number)) : null);
        var source = new Number(42);
        source.EmittedBy = new ModuleInfo(1, "Numbers", "Source");
        var child = source with { Value = 1 };

        var expression = Compile<int>("fam.Source.Value + msg.Value", scope);

        await Assert.That(expression.Evaluate(child)).IsEqualTo(43);
    }

    [Test]
    public async Task Fam_with_an_unknown_module_suggests_one_that_exists()
    {
        var expression = Compile<int>("fam.Sorce.Value", Scope(fam: name => name == "Source" ? new FamTarget(typeof(Number)) : null));

        await Assert.That(expression.ProblemWith(typeof(Number))!.Value.Problem).IsEqualTo("There's no module called Sorce for fam to find. Did you mean 'Source'?");
    }

    [Test]
    public async Task Constant_expressions_are_worked_out_once_without_a_message()
    {
        var syntax = MeeplangParser.ParseExpression("\"https://example.com/\" + (2 * 21)", out _)!;

        var value = ExpressionCompiler.Evaluate(syntax, Scope());

        await Assert.That(value.Value).IsEqualTo("https://example.com/42");
        await Assert.That(ExpressionCompiler.UsesMessage(syntax)).IsFalse();
    }

    [Test]
    public async Task A_constant_cannot_use_msg()
    {
        var syntax = MeeplangParser.ParseExpression("msg.Value", out _)!;

        await Assert.That(() => ExpressionCompiler.Evaluate(syntax, Scope()))
            .Throws<BindException>()
            .WithMessage("msg is only there when a message arrives, so it can't be used here.");
    }
}
