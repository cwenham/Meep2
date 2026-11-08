using System.Collections;
using System.Runtime.CompilerServices;

namespace Meep2.SDK.Messages;

/// <summary>
/// The messages a message was made from. One parent, the common case, is held without allocating.
/// </summary>
/// <remarks>Supports collection expressions, so a module that combines messages can write
/// <c>Parents = [left, right]</c>.</remarks>
[CollectionBuilder(typeof(Parents), nameof(Create))]
public readonly struct Parents : IReadOnlyList<Message>, IEquatable<Parents>
{
    private readonly object? _value; // null, one Message, or a Message[] of two or more

    /// <summary>A single parent.</summary>
    public Parents(Message parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        _value = parent;
    }

    /// <summary>Any number of parents, in order. A combining module puts its first input first.</summary>
    public Parents(IEnumerable<Message> parents)
    {
        ArgumentNullException.ThrowIfNull(parents);
        _value = Pack(parents.ToArray());
    }

    private Parents(object? value) => _value = value;

    /// <summary>No parents.</summary>
    public static Parents None => default;

    /// <summary>Builds parents from a collection expression.</summary>
    public static Parents Create(ReadOnlySpan<Message> parents) => new(Pack(parents.ToArray()));

    /// <summary>How many parents there are.</summary>
    public int Count => _value switch
    {
        null => 0,
        Message[] many => many.Length,
        _ => 1,
    };

    /// <summary>The first parent, or null if there are none.</summary>
    public Message? First => _value switch
    {
        null => null,
        Message[] many => many[0],
        _ => (Message)_value,
    };

    /// <summary>The parent at <paramref name="index"/>.</summary>
    public Message this[int index] => _value switch
    {
        Message[] many => many[index],
        Message one when index == 0 => one,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>Enumerates the parents without allocating.</summary>
    public Enumerator GetEnumerator() => new(_value);

    IEnumerator<Message> IEnumerable<Message>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>True when both hold the same messages in the same order.</summary>
    public bool Equals(Parents other)
    {
        if (ReferenceEquals(_value, other._value))
            return true;
        if (Count != other.Count)
            return false;
        for (var i = 0; i < Count; i++)
        {
            if (!ReferenceEquals(this[i], other[i]))
                return false;
        }
        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Parents other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var parent in this)
            hash.Add(RuntimeHelpers.GetHashCode(parent));
        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() => Count == 1 ? "1 parent" : $"{Count} parents";

    /// <summary>True when both hold the same messages in the same order.</summary>
    public static bool operator ==(Parents left, Parents right) => left.Equals(right);

    /// <summary>True unless both hold the same messages in the same order.</summary>
    public static bool operator !=(Parents left, Parents right) => !left.Equals(right);

    private static object? Pack(Message[] parents)
    {
        foreach (var parent in parents)
            ArgumentNullException.ThrowIfNull(parent, nameof(parents));
        return parents.Length switch
        {
            0 => null,
            1 => parents[0],
            _ => parents,
        };
    }

    /// <summary>Enumerates parents without allocating.</summary>
    public struct Enumerator : IEnumerator<Message>
    {
        private readonly object? _value;
        private readonly int _count;
        private int _index;

        internal Enumerator(object? value)
        {
            _value = value;
            _count = value switch
            {
                null => 0,
                Message[] many => many.Length,
                _ => 1,
            };
            _index = -1;
        }

        /// <inheritdoc/>
        public readonly Message Current => _value is Message[] many ? many[_index] : (Message)_value!;

        readonly object IEnumerator.Current => Current;

        /// <inheritdoc/>
        public bool MoveNext() => ++_index < _count;

        /// <inheritdoc/>
        public void Reset() => _index = -1;

        /// <inheritdoc/>
        public readonly void Dispose()
        {
        }
    }
}
