namespace Meep2.Meeplang.Editing;

/// <summary>A file's text after one edit (<see cref="SourceEditor.Change"/>).</summary>
/// <param name="Text">The new text.</param>
/// <param name="AddedLine">
/// For <see cref="EditKind.Add"/> and <see cref="EditKind.Insert"/>, the line of the new text the new module is
/// written on, counting from 1. Null for other edits.
/// </param>
public sealed record EditedText(string Text, int? AddedLine);
