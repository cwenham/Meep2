using Meep2.SDK.Messages;

namespace Meep2.SDK.Modules;

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
