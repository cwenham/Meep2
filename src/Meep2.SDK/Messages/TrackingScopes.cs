using Meep2.SDK.Modules;

namespace Meep2.SDK.Messages;

/// <summary>
/// The tracking scopes a message counts towards. None, or one, the common cases, are held without allocating; a
/// message made from one parent shares its parent's set.
/// </summary>
internal readonly struct TrackingScopes
{
    private readonly object? _value; // null, one TrackingScope, or a TrackingScope[] of two or more

    /// <summary>A single scope.</summary>
    public TrackingScopes(TrackingScope scope) => _value = scope;

    private TrackingScopes(object? value) => _value = value;

    /// <summary>No scopes.</summary>
    public static TrackingScopes None => default;

    /// <summary>Whether there are no scopes.</summary>
    public bool IsEmpty => _value is null;

    /// <summary>The scopes in either set, each once. Shares one set's storage when the other adds nothing.</summary>
    public TrackingScopes Union(TrackingScopes other)
    {
        if (other._value is null || ReferenceEquals(_value, other._value))
            return this;
        if (_value is null)
            return other;

        var scopes = new List<TrackingScope>();
        AddDistinct(scopes, this);
        AddDistinct(scopes, other);
        return Pack(scopes);
    }

    /// <summary>
    /// The set with <paramref name="scope"/> first. Error scopes go first, so that when a message is released, an
    /// unhandled error is flagged on the other scopes before they finish.
    /// </summary>
    public TrackingScopes Prepend(TrackingScope scope)
    {
        if (_value is null)
            return new(scope);
        var scopes = new List<TrackingScope> { scope };
        AddDistinct(scopes, this);
        return Pack(scopes);
    }

    /// <summary>The set with <paramref name="scope"/> last, unless it's already in it.</summary>
    public TrackingScopes Append(TrackingScope scope)
    {
        if (_value is null || ReferenceEquals(_value, scope))
            return new(scope);
        if (_value is TrackingScope[] existing && Array.IndexOf(existing, scope) >= 0)
            return this;
        var scopes = new List<TrackingScope>();
        AddDistinct(scopes, this);
        scopes.Add(scope);
        return Pack(scopes);
    }

    /// <summary>Counts one more message in flight in each scope.</summary>
    public void AddRef()
    {
        foreach (var scope in this)
            scope.AddRef();
    }

    /// <summary>Counts a message out of each scope, flagging any outcome other than completed first.</summary>
    public void Release(MessageOutcome outcome)
    {
        foreach (var scope in this)
        {
            if (outcome != MessageOutcome.Completed)
                scope.Flag(outcome);
            scope.Release();
        }
    }

    /// <summary>Flags an outcome on each scope.</summary>
    public void Flag(MessageOutcome outcome)
    {
        foreach (var scope in this)
            scope.Flag(outcome);
    }

    /// <summary>Enumerates the scopes without allocating.</summary>
    public Enumerator GetEnumerator() => new(_value);

    private static void AddDistinct(List<TrackingScope> scopes, TrackingScopes set)
    {
        foreach (var scope in set)
        {
            if (!scopes.Contains(scope))
                scopes.Add(scope);
        }
    }

    private static TrackingScopes Pack(List<TrackingScope> scopes) =>
        new(scopes.Count == 1 ? scopes[0] : scopes.ToArray());

    /// <summary>Enumerates scopes without allocating.</summary>
    public struct Enumerator
    {
        private readonly TrackingScope? _one;
        private readonly TrackingScope[]? _many;
        private int _index;

        internal Enumerator(object? value)
        {
            _one = value as TrackingScope;
            _many = value as TrackingScope[];
            _index = -1;
        }

        /// <summary>The scope at the enumerator's place.</summary>
        public readonly TrackingScope Current => _many is null ? _one! : _many[_index];

        /// <summary>Moves to the next scope.</summary>
        public bool MoveNext()
        {
            _index++;
            return _many is null ? _index == 0 && _one is not null : _index < _many.Length;
        }
    }
}
