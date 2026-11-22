namespace Meep2.SDK.Modules;

/// <summary>
/// A value, or why it couldn't be made: what a function returns when failing is ordinary, such as finding a key to
/// sign with, so the caller can nest the call and say what to do with the reason, rather than take a pair apart.
/// </summary>
/// <typeparam name="T">The value's type.</typeparam>
public sealed class ValueOrWhyNot<T>
{
    private readonly T? _value;

    private ValueOrWhyNot(T? value, string? whyNot)
    {
        _value = value;
        WhyNot = whyNot;
    }

    /// <summary>A value that was made.</summary>
    public static ValueOrWhyNot<T> Of(T value) => new(value, null);

    /// <summary>No value, and why not.</summary>
    public static ValueOrWhyNot<T> Not(string whyNot)
    {
        ArgumentException.ThrowIfNullOrEmpty(whyNot);
        return new(default, whyNot);
    }

    /// <summary>Whether there's a value.</summary>
    public bool HasValue => WhyNot is null;

    /// <summary>The value, or the type's default if there isn't one.</summary>
    public T? Value => _value;

    /// <summary>Why there's no value, or null if there is one.</summary>
    public string? WhyNot { get; }

    /// <summary>The value, or the exception <paramref name="failure"/> makes from why not.</summary>
    public T ValueOrThrow(Func<string, Exception> failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return HasValue ? _value! : throw failure(WhyNot!);
    }

    /// <summary>The value, or <paramref name="fallback"/> if there isn't one.</summary>
    public T? ValueOr(T? fallback) => HasValue ? _value : fallback;

    /// <summary>Gives the value if there is one, and otherwise why not.</summary>
    public bool TryGet(out T value, out string whyNot)
    {
        value = _value!;
        whyNot = WhyNot ?? "";
        return HasValue;
    }

    /// <summary>What <paramref name="next"/> makes from the value, or this one's reason if there's no value.</summary>
    public ValueOrWhyNot<TNext> Then<TNext>(Func<T, ValueOrWhyNot<TNext>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return HasValue ? next(_value!) : ValueOrWhyNot<TNext>.Not(WhyNot!);
    }

    /// <summary>What <paramref name="next"/> makes from the value, or this one's reason if there's no value.</summary>
    public async Task<ValueOrWhyNot<TNext>> ThenAsync<TNext>(Func<T, Task<ValueOrWhyNot<TNext>>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return HasValue ? await next(_value!).ConfigureAwait(false) : ValueOrWhyNot<TNext>.Not(WhyNot!);
    }

    /// <summary>The value, or null, and why not, or null, for a caller that wants both at once.</summary>
    public void Deconstruct(out T? value, out string? whyNot)
    {
        value = _value;
        whyNot = WhyNot;
    }

    /// <inheritdoc/>
    public override string ToString() => HasValue ? $"{_value}" : $"no value: {WhyNot}";
}
