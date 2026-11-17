using System.Globalization;
using Meep2.Meeplang.Loading;
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

/// <summary>The verbs a text can use, or null with the reasons when its plugins can't be loaded.</summary>
public delegate Task<(ModuleRegistry? Registry, IReadOnlyList<Diagnostic> Problems)> RegistryLookup(PipelineSyntax syntax, CancellationToken cancellationToken);

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
    private static readonly IEqualityComparer<ModuleSyntax> s_sameLine = ReferenceEqualityComparer.Instance;

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

        var text = syntax;
        PipelineShape? keyed = start; // the text as the edits so far left it, keyed, or null if it doesn't load
        var followed = start; // the last of those texts that loaded
        DraftUpdate? update = null;
        var brokenBy = -1; // the edit after which the text stopped loading
        var tracked = new List<(int Edit, ModuleSyntax Module)>(); // modules the edits wrote, in followed's lines
        var pending = new List<(int Edit, string Line)>(); // and the lines of those written where the text didn't load

        for (var i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            var target = edit;
            if (edit.Kind < EditKind.SetDefault && Earlier(edit.Module) is { } earlier)
            {
                if (keyed is null)
                    throw Broken(i, $"the module edit {earlier + 1} added");
                target = edit with { Module = AddedBy(earlier, edits, i, tracked, keyed) };
            }
            else if (edit.Kind < EditKind.SetDefault && !ByKey(edit, keyed, text, i, edits.Count, out target))
            {
                throw Broken(i, $"the module with the key {edit.Module}");
            }
            EditedText changed;
            try
            {
                changed = SourceEditor.Change(text, target);
            }
            catch (EditException exception)
            {
                throw Failed(i, edits.Count, edit, exception.Message, []);
            }

            var next = parse(changed.Text);
            if (next.HasErrors)
            {
                var errors = next.Diagnostics.Where(d => d.IsError).ToList();
                throw Failed(i, edits.Count, edit, "It would leave text Meeplang can't read: " + Describe(errors, next), errors);
            }
            var (registry, problems) = await registryFor(next, cancellationToken).ConfigureAwait(false);
            update = registry is null ? new DraftUpdate(false, null, problems, new Dictionary<string, string>()) : draft.Preview(next, registry);
            var loaded = update.Loaded ? update.Shape : null;

            if (loaded is not null)
            {
                // Follow the modules written so far into this text, then find the ones this edit wrote.
                var matched = ModuleMatcher.Match(followed.Expanded, loaded.Expanded)
                    .Where(m => m.Old is not null && m.Next is not null)
                    .ToDictionary(m => m.Old!, m => m.Next!, s_sameLine);
                tracked = [.. tracked.Where(t => matched.ContainsKey(t.Module)).Select(t => (t.Edit, matched[t.Module]))];
                var known = matched.Values.Concat(tracked.Select(t => t.Module)).ToHashSet(ReferenceEqualityComparer.Instance);
                foreach (var (edited, line) in pending)
                    tracked.AddRange(loaded.Modules.Where(m => !known.Contains(m.Syntax) && LineOf(next, m.Syntax.Written) is { } written && written.Trim() == line).Select(m => (edited, m.Syntax)));
                pending.Clear();
                if (changed.AddedLine is { } added)
                    tracked.AddRange(loaded.Modules.Where(m => m.Syntax.Written.VerbSpan is { File: null } span && span.Line == added).Select(m => (i, m.Syntax)));
                followed = loaded;
            }
            else
            {
                if (keyed is not null)
                    brokenBy = i;
                if (changed.AddedLine is { } added)
                    pending.Add((i, changed.Text.Split('\n')[added - 1].Trim()));
            }
            text = next;
            keyed = loaded;
        }

        if (update is null)
            throw new DraftEditException(-1, "There are no edits to make.", []);

        // A module can't be found because an edit before it left text that doesn't load: that edit's to blame.
        DraftEditException Broken(int index, string what)
        {
            var errors = update!.Diagnostics.Where(d => d.IsError).ToList();
            return new DraftEditException(brokenBy, $"{Which(brokenBy, edits.Count, edits[brokenBy])} leaves text that doesn't load: {Describe(errors, text)} So {Which(index, edits.Count, edits[index], capital: false)} can't find {what}. Name it by its name or number, or put the edits in an order that keeps the text loading. Nothing was changed.", errors);
        }
        if (keyed is null)
        {
            var errors = update.Diagnostics.Where(d => d.IsError).ToList();
            var message = $"After the edits the text wouldn't load: {Describe(errors, text)}";
            if (brokenBy >= 0)
                message = $"{Which(brokenBy, edits.Count, edits[brokenBy])} leaves text that doesn't load, and no edit after it mends it: {Describe(errors, text)}";
            throw new DraftEditException(brokenBy, message + " Nothing was changed.", errors);
        }

        var keys = keyed.Modules.ToDictionary(m => m.Syntax, m => m.Key, s_sameLine);
        var addedModules = tracked
            .OrderBy(t => t.Edit)
            .ThenBy(t => keyed.Modules.ToList().FindIndex(m => ReferenceEquals(m.Syntax, t.Module)))
            .Select(t => new AddedModule(t.Edit, keys[t.Module]))
            .ToList();
        return new DraftEdits(text, update, addedModules);
    }

    // An edit that names its module by key, as SourceEditor names it: by its number in the text as it is now. False
    // if it names one by key and the text doesn't load, so has no keys.
    private static bool ByKey(SourceEdit edit, PipelineShape? keyed, PipelineSyntax text, int index, int count, out SourceEdit target)
    {
        target = edit;
        if (!IsKey(edit.Module))
            return true;
        if (keyed?.Modules.FirstOrDefault(m => m.Key == edit.Module) is { } module)
        {
            target = edit with { Module = module.Info.Number.ToString(CultureInfo.InvariantCulture) };
            return true;
        }

        // A module may be called m3 with as; then the name names it.
        try
        {
            SourceEditor.Find(text, edit.Module);
            return true;
        }
        catch (EditException)
        {
        }
        if (keyed is null)
            return false;
        var after = index == 0 ? "" : " after the edits before it";
        throw Failed(index, count, edit, $"There's no module with the key {edit.Module}{after}. The keys are {string.Join(", ", keyed.Modules.Select(m => $"{m.Key} ({m.Info})"))}.", []);
    }

    // +N: the module edit N (counting from 0) added, as added names it (Q198).
    private static int? Earlier(string module) =>
        module is ['+', >= '0' and <= '9', ..] && int.TryParse(module.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : null;

    // The module an earlier edit added, as SourceEditor names it: by its number in the text as it is now. An edit that
    // writes a template's line adds a module for each use of the template; they're copies of one line, which an edit
    // to any of them changes, so the first names them all.
    private static string AddedBy(int earlier, IReadOnlyList<SourceEdit> edits, int index, List<(int Edit, ModuleSyntax Module)> tracked, PipelineShape keyed)
    {
        var edit = edits[index];
        if (earlier >= index)
            throw Failed(index, edits.Count, edit, $"It names +{earlier}, the module edit {earlier + 1} adds, but only an edit before it can add a module it names.", []);
        if (edits[earlier].Kind is not (EditKind.Add or EditKind.Insert))
            throw Failed(index, edits.Count, edit, $"It names +{earlier}, the module edit {earlier + 1} adds, but that edit ({edits[earlier]}) doesn't add a module: only add and insert do.", []);
        var module = keyed.Modules.FirstOrDefault(m => tracked.Any(t => t.Edit == earlier && ReferenceEquals(t.Module, m.Syntax)))
            ?? throw Failed(index, edits.Count, edit, $"It names +{earlier}, the module edit {earlier + 1} added, but an edit after that took it out.", []);
        return module.Info.Number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Whether an edit names its module in a way only <see cref="DraftEditor"/> knows: by a key, or as <c>+N</c>, the
    /// module an earlier edit added. <see cref="SourceEditor"/> knows the rest.
    /// </summary>
    public static bool NamesByKey(string module) => IsKey(module) || Earlier(module) is not null;

    /// <summary>Whether a module's name is in the form of a key, <c>m</c> and a number.</summary>
    public static bool IsKey(string module) =>
        module is ['m', >= '1' and <= '9', ..] && module.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    private static DraftEditException Failed(int index, int count, SourceEdit edit, string why, IReadOnlyList<Diagnostic> problems) =>
        new(index, $"{Which(index, count, edit)} can't be made. {why} Nothing was changed.", problems);

    private static string Which(int index, int count, SourceEdit edit, bool capital = true) =>
        (count == 1 ? $"the edit ({edit})" : $"edit {index + 1} of {count} ({edit})") is var which && capital
            ? char.ToUpperInvariant(which[0]) + which[1..]
            : which;

    // Problems, each with the line it's on, since the text they're in is one the client hasn't seen.
    private static string Describe(IReadOnlyList<Diagnostic> problems, PipelineSyntax text)
    {
        if (problems.Count == 0)
            return "its plugins can't be loaded.";
        var lines = text.Source.Text.Split('\n');
        return string.Join(" ", problems.Select(p =>
            p.Span.File is null && p.Span.Line >= 1 && p.Span.Line <= lines.Length
                ? $"{p.Message} (line {p.Span.Line}: {lines[p.Span.Line - 1].Trim()})"
                : p.ToString()));
    }

    // A module's line as written, if it's in the pipeline's own file.
    private static string? LineOf(PipelineSyntax text, ModuleSyntax module)
    {
        if (module.VerbSpan.File is not null)
            return null;
        var lines = text.Source.Text.Split('\n');
        return module.VerbSpan.Line >= 1 && module.VerbSpan.Line <= lines.Length ? lines[module.VerbSpan.Line - 1] : null;
    }
}
