namespace Meep2.Daemon;

/// <summary>One edit to a running pipeline's text, as the API takes it (goal 4).</summary>
/// <param name="Module">The module: its name, its number, or a verb only one module has. Empty for edits to the defaults block, constants and plugins.</param>
/// <param name="Kind">
/// set, unset, set-modifier, remove-modifier, rename, add, insert, remove or remove-branch for a module; set-default,
/// unset-default, set-const, unset-const, add-plugin or remove-plugin for the rest of the file (Q125).
/// </param>
/// <param name="Name">The parameter or modifier, for those edits.</param>
/// <param name="Value">The value as Meeplang text, the new name, or the new module's line.</param>
public sealed record EditItem(string? Module, string Kind, string? Name = null, string? Value = null);

/// <summary>Edits to make to a running pipeline, and where to commit its new text.</summary>
/// <param name="Edits">The edits, made one after another.</param>
/// <param name="Message">The commit message, or null for one that lists the edits.</param>
/// <param name="Branch">The branch to commit to, <c>pending</c> unless given (Q32).</param>
/// <param name="Save">Whether to commit the new text at all.</param>
public sealed record EditRequest(IReadOnlyList<EditItem> Edits, string? Message = null, string? Branch = null, bool Save = true);

/// <summary>A commit that write-back made.</summary>
public sealed record SavedCommit(string Branch, string Sha, string Repository, string Path)
{
    /// <summary>Why it isn't signed with your SSH key, if it isn't (Q126).</summary>
    public string? Unsigned { get; init; }
}

/// <summary>What a reload or an edit did to a running pipeline.</summary>
/// <param name="Applied">Whether the pipeline changed into the new text. If not, the diagnostics say why, and it runs as it was.</param>
/// <param name="Added">Modules added.</param>
/// <param name="Removed">Modules taken out.</param>
/// <param name="Reconfigured">Modules that took new parameters and kept their state.</param>
/// <param name="Replaced">Modules made again with new instances.</param>
/// <param name="Unchanged">Modules that carried on as they were.</param>
/// <param name="Diagnostics">Problems with the new text, and warnings.</param>
public sealed record ChangeResult(
    bool Applied,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Reconfigured,
    IReadOnlyList<string> Replaced,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>The commit write-back made, if it made one.</summary>
    public SavedCommit? Saved { get; init; }

    /// <summary>Why the new text wasn't committed, if it wasn't.</summary>
    public string? NotSaved { get; init; }

    /// <summary>The file the new text was written to, for a file that isn't in a Git repository (Q124).</summary>
    public string? WrittenTo { get; init; }

    /// <summary>The pipeline's text now, after an edit.</summary>
    public string? Text { get; init; }

    /// <summary>Whether the pipeline's own settings, under <c>defaults</c>, changed.</summary>
    public bool SettingsChanged { get; init; }
}

/// <summary>The text a running pipeline runs from.</summary>
/// <param name="Path">The file it came from.</param>
/// <param name="Text">Its text now: the file as loaded or last reloaded, with any edits made through the API since.</param>
/// <param name="Edited">Whether edits made through the API make it differ from the file as loaded.</param>
public sealed record SourceText(string Path, string Text, bool Edited);
