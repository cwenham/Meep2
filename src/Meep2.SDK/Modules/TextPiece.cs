namespace Meep2.SDK.Modules;

/// <summary>A piece of a value worked out for a message: text written in the pipeline, or text the message gave.</summary>
/// <param name="Text">The piece's text.</param>
/// <param name="FromMessage">Whether the message decided it, as a <c>{msg…}</c> hole does.</param>
public readonly record struct TextPiece(string Text, bool FromMessage);
