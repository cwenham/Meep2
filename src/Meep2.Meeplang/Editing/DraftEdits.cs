using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Editing;

/// <summary>A module an <c>add</c> or <c>insert</c> edit writes, and the key it will have.</summary>
/// <param name="Edit">Which edit wrote it, counting from 0 through the list.</param>
/// <param name="Key">The key it has once the text is changed.</param>
public sealed record AddedModule(int Edit, string Key);

/// <summary>What a list of edits would make of a draft's text (<see cref="DraftEditor.ApplyAsync"/>).</summary>
/// <param name="Syntax">The text after the edits, parsed.</param>
/// <param name="Update">Its shape, keyed as <see cref="PipelineDraft.Update"/> will key it.</param>
/// <param name="Added">
/// The modules the <c>add</c> and <c>insert</c> edits wrote, with their keys, in the order of the edits. A template's
/// line is copied into each of its uses, so a module added to a template is one for each use.
/// </param>
public sealed record DraftEdits(PipelineSyntax Syntax, DraftUpdate Update, IReadOnlyList<AddedModule> Added);
