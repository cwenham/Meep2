using Meep2.SDK.Messages;

namespace Meep2.SDK.Modules;

/// <summary>A piece of a value worked out for a message: text written in the pipeline, or text the message gave.</summary>
/// <param name="Text">The piece's text.</param>
/// <param name="FromMessage">Whether the message decided it, as a <c>{msg…}</c> hole does.</param>
public readonly record struct TextPiece(string Text, bool FromMessage);

/// <summary>
/// A <see cref="Computed{T}"/> text that can say which of its pieces came from the message, so a module can treat
/// those with suspicion, as Save does with paths. Meeplang's templates implement it.
/// </summary>
public interface IComputedPieces
{
    /// <summary>Works out the value for one message, in pieces that join up to what <c>Evaluate</c> gives.</summary>
    /// <exception cref="EvaluationException">The value couldn't be worked out for this message.</exception>
    IReadOnlyList<TextPiece> Pieces(Message message);
}

/// <summary>Helpers for computed text.</summary>
public static class ComputedText
{
    /// <summary>
    /// Works out a text for one message, in pieces. A value that doesn't say how it was made is one piece, from the
    /// message unless it's the same for every message.
    /// </summary>
    /// <exception cref="EvaluationException">The value couldn't be worked out for this message.</exception>
    public static IReadOnlyList<TextPiece> Pieces(this Computed<string> computed, Message message)
    {
        ArgumentNullException.ThrowIfNull(computed);
        return computed is IComputedPieces pieces
            ? pieces.Pieces(message)
            : [new TextPiece(computed.Evaluate(message), FromMessage: !computed.IsConstant)];
    }
}
