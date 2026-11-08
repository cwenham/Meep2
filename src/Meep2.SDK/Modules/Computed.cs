using Meep2.SDK.Messages;

namespace Meep2.SDK.Modules;

/// <summary>
/// A parameter worked out for each message, such as a URL template or a condition. Meeplang compiles these from the
/// pipeline file; C# code can make them with <see cref="Constant"/> or <see cref="From"/>.
/// </summary>
/// <typeparam name="T">The type of value it works out.</typeparam>
public abstract class Computed<T>
{
    /// <summary>Whether the value is the same for every message.</summary>
    public virtual bool IsConstant => false;

    /// <summary>Works out the value for one message.</summary>
    /// <exception cref="EvaluationException">
    /// The value couldn't be worked out for this message, for example because a member it reads isn't there.
    /// Modules should catch this and emit an error, rather than let it escape.
    /// </exception>
    public abstract T Evaluate(Message message);

    /// <summary>
    /// Whether the value can be worked out for messages of this type. Messages it can't be worked out for skip the
    /// module, as they would with <c>@bypass</c>.
    /// </summary>
    public virtual bool AppliesTo(Type messageType) => true;

    /// <summary>A value that's the same for every message.</summary>
    public static Computed<T> Constant(T value) => new ConstantValue(value);

    /// <summary>A value worked out by a C# function.</summary>
    public static Computed<T> From(Func<Message, T> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new FunctionValue(function);
    }

    private sealed class ConstantValue(T value) : Computed<T>
    {
        public override bool IsConstant => true;

        public override T Evaluate(Message message) => value;

        public override string? ToString() => value?.ToString();
    }

    private sealed class FunctionValue(Func<Message, T> function) : Computed<T>
    {
        public override T Evaluate(Message message) => function(message);
    }
}

/// <summary>A <see cref="Computed{T}"/> value couldn't be worked out for a message.</summary>
public sealed class EvaluationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public EvaluationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the exception that caused it.</summary>
    public EvaluationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
