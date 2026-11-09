using System.Collections.Concurrent;
using Meep2.Meeplang.Syntax;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Compilation;

/// <summary>A compiled expression the loader can check against message types before the pipeline runs.</summary>
internal interface ICheckedComputed
{
    /// <summary>The expression as written.</summary>
    string Text { get; }

    /// <summary>Whether the expression fits messages of this type.</summary>
    bool AppliesTo(Type messageType);

    /// <summary>Why the expression doesn't fit messages of this type, and where, or null if it does.</summary>
    (string Problem, SourceSpan Span)? ProblemWith(Type messageType);

    /// <summary>
    /// Which messages the parameter is worked out for: null for all, true for errors only, false for the rest
    /// (<see cref="ForErrorsAttribute"/>).
    /// </summary>
    bool? ForErrors { get; set; }
}

/// <summary>Checks expressions against message types, minding which messages each is worked out for.</summary>
internal static class CheckedComputed
{
    /// <summary>Whether the parameter is worked out for messages of this type.</summary>
    public static bool IsUsedFor(this ICheckedComputed computed, Type messageType) =>
        computed.ForErrors is not { } errors || errors == typeof(ErrorMessage).IsAssignableFrom(messageType);

    /// <summary>Whether messages of this type can go into the module as far as this parameter is concerned.</summary>
    public static bool Fits(this ICheckedComputed computed, Type messageType) =>
        !computed.IsUsedFor(messageType) || computed.AppliesTo(messageType);
}

/// <summary>
/// A parameter compiled from a Meeplang expression. It's compiled separately for each message type it meets, the
/// first time it meets it, and the result is remembered. Types it doesn't fit skip the module.
/// </summary>
/// <remarks>
/// Sums are compiled to work in decimal. The first time one's answer doesn't fit, the expression is compiled again
/// for that message type to work with big numbers (<see cref="Numbers.Arithmetic"/>), and that version is kept: it's
/// slower, but a source of numbers that big is likely to send more.
/// </remarks>
internal sealed class CompiledComputed<T>(ExpressionSyntax syntax, CompileScope scope, string text) : Computed<T>, ICheckedComputed
{
    private readonly ConcurrentDictionary<Type, Compiled> _byType = new();
    private volatile Compiled? _last;

    public string Text => text;

    public bool? ForErrors { get; set; }

    public override T Evaluate(Message message)
    {
        var compiled = For(message.GetType());
        if (compiled.Function is null)
            throw new EvaluationException($"{text} can't be worked out for {ExpressionBinder.FriendlyName(message.GetType())}: {compiled.Problem}");
        try
        {
            return compiled.Function(message);
        }
        catch (BeyondDecimalException)
        {
            return Run(UseBigNumbers(compiled), message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw Failed(exception);
        }
    }

    public override bool AppliesTo(Type messageType) => For(messageType).Function is not null;

    public (string Problem, SourceSpan Span)? ProblemWith(Type messageType)
    {
        var compiled = For(messageType);
        return compiled.Function is null ? (compiled.Problem!, compiled.ProblemSpan) : null;
    }

    public override string ToString() => text;

    private Compiled For(Type messageType)
    {
        var last = _last;
        if (last is not null && last.Type == messageType)
            return last;
        var compiled = _byType.GetOrAdd(messageType, Compile);
        _last = compiled;
        return compiled;
    }

    private Compiled Compile(Type messageType)
    {
        try
        {
            return new Compiled(messageType, ExpressionCompiler.Compile<T>(syntax, scope, messageType), null, default);
        }
        catch (BindException exception)
        {
            return new Compiled(messageType, null, exception.Message, exception.Span);
        }
    }

    // Compiles the expression again for big numbers, and keeps that version for this message type.
    private Compiled UseBigNumbers(Compiled compiled)
    {
        Compiled big;
        try
        {
            big = compiled with { Function = ExpressionCompiler.Compile<T>(syntax, scope, compiled.Type, bigNumbers: true), BigNumbers = true };
        }
        catch (BindException exception)
        {
            throw new EvaluationException($"Couldn't work out {text} with big numbers: {exception.Message}", exception);
        }
        _byType[compiled.Type] = big;
        _last = big;
        return big;
    }

    private T Run(Compiled compiled, Message message)
    {
        try
        {
            return compiled.Function!(message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw Failed(exception);
        }
    }

    private EvaluationException Failed(Exception exception)
    {
        var reason = exception is EvaluationException ? exception.Message : $"{exception.GetType().Name}: {exception.Message}";
        return new EvaluationException($"Couldn't work out {text}: {reason}", exception);
    }

    private sealed record Compiled(Type Type, Func<Message, T>? Function, string? Problem, SourceSpan ProblemSpan)
    {
        /// <summary>Whether sums work with big numbers, after one went beyond decimal for this message type.</summary>
        public bool BigNumbers { get; init; }
    }
}
