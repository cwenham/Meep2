using Meep2.Meeplang.Syntax;
using Meep2.SDK.Modules;

namespace Meep2.Tests;

public class ValueOrWhyNotTests
{
    [Test]
    public async Task A_value_carries_on_through_then_and_a_reason_passes_by()
    {
        var found = ValueOrWhyNot<string>.Of("key");
        var missing = ValueOrWhyNot<string>.Not("there's no key");

        await Assert.That((await found.ThenAsync(k => Task.FromResult(ValueOrWhyNot<int>.Of(k.Length)))).Value).IsEqualTo(3);
        var passed = missing.Then(k => ValueOrWhyNot<int>.Of(k.Length));
        await Assert.That(passed.HasValue).IsFalse();
        await Assert.That(passed.WhyNot).IsEqualTo("there's no key");
        await Assert.That(missing.ValueOr("default")).IsEqualTo("default");
        await Assert.That(missing.TryGet(out _, out var why)).IsFalse();
        await Assert.That(why).IsEqualTo("there's no key");
        await Assert.That(() => missing.ValueOrThrow(reason => new InvalidOperationException(reason))).Throws<InvalidOperationException>().WithMessage("there's no key");
        await Assert.That(found.ValueOrThrow(reason => new InvalidOperationException(reason))).IsEqualTo("key");
    }

    [Test]
    public async Task A_load_without_a_value_throws_with_its_errors_alone()
    {
        var span = new SourceSpan(1, 1, 1);
        var failed = new WithDiagnostics<string>(null, [new Diagnostic(DiagnosticSeverity.Warning, "careful", span, null), new Diagnostic(DiagnosticSeverity.Error, "broken", span, "a.meep")]);

        await Assert.That(failed.HasValue).IsFalse();
        await Assert.That(failed.Errors.Count()).IsEqualTo(1);
        await Assert.That(() => failed.ValueOrThrow(text => new InvalidOperationException(text))).Throws<InvalidOperationException>().WithMessage("a.meep(1,1): error: broken");
        await Assert.That(new WithDiagnostics<string>("made", []).ValueOrThrow(text => new InvalidOperationException(text))).IsEqualTo("made");
    }
}
