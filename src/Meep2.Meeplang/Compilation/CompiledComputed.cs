using System.Collections.Concurrent;
using Meep2.Meeplang.Syntax;
using Meep2.SDK.Messages;
using Meep2.SDK.Modules;

namespace Meep2.Meeplang.Compilation;

/// <summary>
/// A parameter compiled from a Meeplang expression. It's compiled separately for each message type it meets, the
/// first time it meets it, and the result is remembered. Types it doesn't fit skip the module.
/// </summary>
/// <remarks>
/// Sums are compiled to work in decimal. The first time one's answer doesn't fit, the expression is compiled again
/// for that message type to work with big numbers (<see cref="Numbers.Arithmetic"/>), and that version is kept: it's
/// slower, but a source of numbers that big is likely to send more.
/// </remarks>
internal sealed class CompiledComputed<T>(ExpressionSyntax syntax, CompileScope scope, string text) : Computed<T>, ICheckedComputed, IComputedPieces
{
    private readonly ConcurrentDictionary<Type, Compiled> _byType = new();
    private readonly ConcurrentDictionary<Type, Func<Message, string[]>> _piecesByType = new();
    private volatile Compiled? _last;

    /// <summary>
    /// The value in pieces: for a template, its text and each hole, marking the holes that read the message; for
    /// anything else, one piece, from the message if the expression reads it.
    /// </summary>
    public IReadOnlyList<TextPiece> Pieces(Message message)
    {
        if (syntax is not TemplateSyntax template)
            return [new TextPiece(Evaluate(message)?.ToString() ?? "", ExpressionCompiler.UsesMessage(syntax))];

        // Checked first, so a template that doesn't fit this message type fails as Evaluate would.
        var compiled = For(message.GetType());
        if (compiled.Function is null)
            throw new EvaluationException($"{text} can't be worked out for {ExpressionBinder.FriendlyName(message.GetType())}: {compiled.Problem}");
        string[] parts;
        try
        {
            parts = _piecesByType.GetOrAdd(message.GetType(), type => ExpressionCompiler.CompilePieces(template, scope, type, compiled.BigNumbers))(message);
        }
        catch (BeyondDecimalException)
        {
            var big = ExpressionCompiler.CompilePieces(template, scope, message.GetType(), bigNumbers: true);
            _piecesByType[message.GetType()] = big;
            parts = RunPieces(big, message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw Failed(exception);
        }
        var pieces = new TextPiece[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            pieces[i] = new TextPiece(parts[i], template.Parts[i] is HolePart hole && ExpressionCompiler.UsesMessage(hole.Expression));
        return pieces;
    }

    private string[] RunPieces(Func<Message, string[]> function, Message message)
    {
        try
        {
            return function(message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw Failed(exception);
        }
    }

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
