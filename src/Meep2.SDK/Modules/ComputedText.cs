using Meep2.SDK.Messages;

namespace Meep2.SDK.Modules;

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
