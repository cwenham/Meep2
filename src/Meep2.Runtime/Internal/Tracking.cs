using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Runtime.Internal;

/// <summary>Reports how a tracked message fared once it and its descendants have finished.</summary>
internal sealed class OutcomeScope : TrackingScope
{
    private readonly TaskCompletionSource<MessageOutcome> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<MessageOutcome> Task => _finished.Task;

    protected override void OnFinished(MessageOutcome outcome) => _finished.TrySetResult(outcome);
}

/// <summary>Reports an error that nothing handled, once it and its descendants have finished.</summary>
internal sealed class ErrorScope(ErrorMessage error, Pipeline pipeline) : TrackingScope
{
    protected override void OnFinished(MessageOutcome outcome)
    {
        if (!error.IsHandled)
            pipeline.ReportUnhandled(error);
    }
}

/// <summary>
/// Works with the scopes on a message, held as null, a single scope, or an array, so the common cases don't allocate.
/// A message made from one parent shares its parent's set.
/// </summary>
internal static class ScopeSet
{
    public static object? Union(object? first, object? second)
    {
        if (second is null || ReferenceEquals(first, second))
            return first;
        if (first is null)
            return second;

        var scopes = new List<TrackingScope>();
        AddDistinct(scopes, first);
        AddDistinct(scopes, second);
        return Pack(scopes);
    }

    // Error scopes go first, so that when a message is released, an unhandled error is flagged on the other scopes
    // before they finish.
    public static object Prepend(TrackingScope scope, object? set)
    {
        if (set is null)
            return scope;
        var scopes = new List<TrackingScope> { scope };
        AddDistinct(scopes, set);
        return Pack(scopes);
    }

    public static object Append(object? set, TrackingScope scope)
    {
        if (set is null || ReferenceEquals(set, scope))
            return scope;
        if (set is TrackingScope[] existing && Array.IndexOf(existing, scope) >= 0)
            return set;
        var scopes = new List<TrackingScope>();
        AddDistinct(scopes, set);
        scopes.Add(scope);
        return Pack(scopes);
    }

    public static void AddRef(object? set)
    {
        switch (set)
        {
            case null:
                return;
            case TrackingScope scope:
                scope.AddRef();
                return;
            default:
                foreach (var scope in (TrackingScope[])set)
                    scope.AddRef();
                return;
        }
    }

    public static void Release(object? set, MessageOutcome outcome)
    {
        switch (set)
        {
            case null:
                return;
            case TrackingScope scope:
                Release(scope, outcome);
                return;
            default:
                foreach (var scope in (TrackingScope[])set)
                    Release(scope, outcome);
                return;
        }
    }

    public static void Flag(object? set, MessageOutcome outcome)
    {
        switch (set)
        {
            case null:
                return;
            case TrackingScope scope:
                scope.Flag(outcome);
                return;
            default:
                foreach (var scope in (TrackingScope[])set)
                    scope.Flag(outcome);
                return;
        }
    }

    public static void ReleaseAll(List<TrackingScope>? scopes)
    {
        if (scopes is null)
            return;
        foreach (var scope in scopes)
            scope.Release();
    }

    private static void Release(TrackingScope scope, MessageOutcome outcome)
    {
        if (outcome != MessageOutcome.Completed)
            scope.Flag(outcome);
        scope.Release();
    }

    private static void AddDistinct(List<TrackingScope> scopes, object set)
    {
        if (set is TrackingScope one)
        {
            if (!scopes.Contains(one))
                scopes.Add(one);
            return;
        }
        foreach (var scope in (TrackingScope[])set)
        {
            if (!scopes.Contains(scope))
                scopes.Add(scope);
        }
    }

    private static object Pack(List<TrackingScope> scopes) => scopes.Count == 1 ? scopes[0] : scopes.ToArray();
}

/// <summary>
/// Counts everything that keeps a pipeline alive: messages in buffers and handlers, running sources, and the
/// startup hold. The pipeline halts when it reaches zero.
/// </summary>
internal sealed class InFlightCounter
{
    private const long HaltedCount = long.MinValue / 2;

    private long _count;

    public void Add() => Interlocked.Increment(ref _count);

    /// <summary>Releases one count. Returns true exactly once: when the count first reaches zero.</summary>
    public bool Release() =>
        Interlocked.Decrement(ref _count) == 0 && Interlocked.CompareExchange(ref _count, HaltedCount, 0) == 0;
}
