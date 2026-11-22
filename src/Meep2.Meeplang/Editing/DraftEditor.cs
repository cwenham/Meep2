using System.Globalization;
using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Editing;

/// <summary>A list of edits couldn't be made. The message says which edit, and why.</summary>
/// <param name="edit">The edit to blame, counting from 0, or -1 if it isn't one edit's doing.</param>
/// <param name="message">What went wrong, for a person.</param>
/// <param name="problems">What Meeplang found wrong with the text the edits made, if that's why.</param>
public sealed class DraftEditException(int edit, string message, IReadOnlyList<Diagnostic> problems) : Exception(message)
{
    /// <summary>The edit to blame, counting from 0 through the list, or -1 if it isn't one edit's doing.</summary>
    public int Edit { get; } = edit;

    /// <summary>What Meeplang found wrong with the text the edits made, if that's why.</summary>
    public IReadOnlyList<Diagnostic> Problems { get; } = problems;
}

/// <summary>
/// Makes a list of <see cref="SourceEditor"/> edits to a draft's text without changing the draft, for an editor that
/// applies the new text itself (<c>meep lsp</c>'s <c>meep/applyEdits</c>): each module the edits add, with the key it
/// will have once the editor sends the new text, and the new text's shape for a preview.
/// </summary>
/// <remarks>
/// <para>
/// Edits are made one after another, each on the text the ones before it left, and each can name its module by its
/// key in the draft (<c>m3</c>) as well as by name or number. A key names the module that has it in the text as the
/// edits before left it, keyed as the draft would key that text: so <c>m3</c> is still the same module after an edit
/// above it renumbers it. An edit can also name the module an earlier <c>add</c> or <c>insert</c> in the list wrote,
/// as <c>+N</c>, counting from 0 as <see cref="DraftEdits.Added"/> does, since its key isn't known until the answer.
/// </para>
/// <para>
/// The list is all or nothing: an edit that can't be made, or that leaves text that doesn't parse, stops it, and so
/// does a list whose text doesn't load at the end. The text may fail to load part way through, as when a module
/// using a plugin is added before the plugin's line, but keys can't name modules there.
/// </para>
/// </remarks>
public static class DraftEditor
{
    /// <summary>Makes the edits to the draft's text as it is, and keys the result as the draft will.</summary>
    /// <param name="draft">The draft, whose last text that loaded is <paramref name="syntax"/>.</param>
    /// <param name="syntax">The text now, parsed. It must be the text the draft last loaded.</param>
    /// <param name="edits">The edits, made in order.</param>
    /// <param name="parse">Parses a new text, reading the files it includes as the draft's text was read.</param>
    /// <param name="registryFor">The verbs each new text can use.</param>
    /// <param name="cancellationToken">Stops loading plugins.</param>
    /// <exception cref="DraftEditException">An edit can't be made, or the text it leaves doesn't load.</exception>
    public static async Task<DraftEdits> ApplyAsync(
        PipelineDraft draft,
        PipelineSyntax syntax,
        IReadOnlyList<SourceEdit> edits,
        Func<string, PipelineSyntax> parse,
        RegistryLookup registryFor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(registryFor);
        if (draft.Shape is not { } start || start.Syntax.Source.Text != syntax.Source.Text)
            throw new DraftEditException(-1, "The text doesn't load as it is, so its modules can't be found. Edits can be made once it loads.", []);
        return await new DraftEditRun(draft, start, syntax, edits, parse, registryFor).RunAsync(cancellationToken).ConfigureAwait(false);
    }

    // +N: the module edit N (counting from 0) added, as added names it (Q198).
    internal static int? Earlier(string module) =>
        module is ['+', >= '0' and <= '9', ..] && int.TryParse(module.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : null;

    /// <summary>
    /// Whether an edit names its module in a way only <see cref="DraftEditor"/> knows: by a key, or as <c>+N</c>, the
    /// module an earlier edit added. <see cref="SourceEditor"/> knows the rest.
    /// </summary>
    public static bool NamesByKey(string module) => IsKey(module) || Earlier(module) is not null;

    /// <summary>Whether a module's name is in the form of a key, <c>m</c> and a number.</summary>
    public static bool IsKey(string module) =>
        module is ['m', >= '1' and <= '9', ..] && module.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;
}
