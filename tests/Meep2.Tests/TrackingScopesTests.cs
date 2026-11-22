using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Tests;

public class TrackingScopesTests
{
    private sealed class Scope : TrackingScope
    {
        public MessageOutcome? Finished { get; private set; }

        protected override void OnFinished(MessageOutcome outcome) => Finished = outcome;
    }

    private static List<TrackingScope> Listed(TrackingScopes scopes)
    {
        var list = new List<TrackingScope>();
        foreach (var scope in scopes)
            list.Add(scope);
        return list;
    }

    [Test]
    public async Task No_scopes_enumerate_as_none()
    {
        await Assert.That(TrackingScopes.None.IsEmpty).IsTrue();
        await Assert.That(Listed(TrackingScopes.None)).IsEmpty();
    }

    [Test]
    public async Task A_union_keeps_each_scope_once_in_order()
    {
        var a = new Scope();
        var b = new Scope();
        var c = new Scope();
        var left = new TrackingScopes(a).Append(b);
        var right = new TrackingScopes(b).Append(c);

        await Assert.That(Listed(left.Union(right))).IsEquivalentTo(new TrackingScope[] { a, b, c }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Listed(left.Union(TrackingScopes.None))).IsEquivalentTo(new TrackingScope[] { a, b }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Listed(TrackingScopes.None.Union(right))).IsEquivalentTo(new TrackingScope[] { b, c }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Prepend_puts_a_scope_first_and_append_adds_it_once()
    {
        var a = new Scope();
        var b = new Scope();
        var error = new Scope();
        var set = new TrackingScopes(a).Append(b).Append(a).Append(b);

        await Assert.That(Listed(set)).IsEquivalentTo(new TrackingScope[] { a, b }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Listed(set.Prepend(error))).IsEquivalentTo(new TrackingScope[] { error, a, b }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Listed(new TrackingScopes(a).Append(a))).IsEquivalentTo(new TrackingScope[] { a }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Releasing_flags_the_outcome_on_every_scope_before_it_finishes()
    {
        var a = new Scope();
        var b = new Scope();
        var set = new TrackingScopes(a).Append(b);
        set.AddRef();
        a.Release();
        b.Release();

        set.Release(MessageOutcome.Dropped);

        await Assert.That(a.Finished).IsEqualTo(MessageOutcome.Dropped);
        await Assert.That(b.Finished).IsEqualTo(MessageOutcome.Dropped);
    }
}
