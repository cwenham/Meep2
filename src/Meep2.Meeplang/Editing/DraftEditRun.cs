using System.Globalization;
using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang.Editing;

/// <summary>
/// One list of edits being made to a draft's text (<see cref="DraftEditor.ApplyAsync"/>): each edit made on the text
/// the ones before it left, following the modules the edits wrote through each text that loads, so keys and +N find
/// them, and blaming the right edit when one can't be made.
/// </summary>
internal sealed class DraftEditRun
{
    private static readonly IEqualityComparer<ModuleSyntax> s_sameLine = ReferenceEqualityComparer.Instance;

    private readonly PipelineDraft _draft;
    private readonly IReadOnlyList<SourceEdit> _edits;
    private readonly Func<string, PipelineSyntax> _parse;
    private readonly RegistryLookup _registryFor;

    private PipelineSyntax _text;
    private PipelineShape? _keyed; // the text as the edits so far left it, keyed, or null if it doesn't load
    private PipelineShape _followed; // the last of those texts that loaded
    private DraftUpdate? _update;
    private int _brokenBy = -1; // the edit after which the text stopped loading
    private List<Tracked> _tracked = []; // modules the edits wrote, in _followed's lines
    private readonly List<Pending> _pending = []; // and the lines of those written where the text didn't load

    /// <summary>Starts from a draft whose last text that loaded is <paramref name="syntax"/>.</summary>
    public DraftEditRun(PipelineDraft draft, PipelineSyntax syntax, IReadOnlyList<SourceEdit> edits, Func<string, PipelineSyntax> parse, RegistryLookup registryFor)
    {
        var start = draft.Shape ?? throw new ArgumentException("The draft's text doesn't load.", nameof(draft));
        _draft = draft;
        _edits = edits;
        _parse = parse;
        _registryFor = registryFor;
        _text = syntax;
        _keyed = start;
        _followed = start;
    }

    /// <summary>Makes every edit, in order, and gives the text they make, its update and the modules they added.</summary>
    /// <exception cref="DraftEditException">An edit couldn't be made, or the text they make doesn't load.</exception>
    public async Task<DraftEdits> RunAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < _edits.Count; i++)
            await MakeAsync(i, cancellationToken).ConfigureAwait(false);
        if (_update is null)
            throw new DraftEditException(-1, "There are no edits to make.", []);
        if (_keyed is not { } keyed)
            throw DoesNotLoad();
        var keys = keyed.Modules.ToDictionary(m => m.Syntax, m => m.Key, s_sameLine);
        var added = _tracked
            .OrderBy(t => t.Edit)
            .ThenBy(t => keyed.Modules.ToList().FindIndex(m => ReferenceEquals(m.Syntax, t.Module)))
            .Select(t => new AddedModule(t.Edit, keys[t.Module]))
            .ToList();
        return new DraftEdits(_text, _update, added);
    }

    // Makes edit i, on the text the edits before it left.
    private async Task MakeAsync(int i, CancellationToken cancellationToken)
    {
        var edit = _edits[i];
        EditedText changed;
        try
        {
            changed = SourceEditor.Change(_text, Target(i));
        }
        catch (EditException exception)
        {
            throw Failed(i, exception.Message, []);
        }

        var next = _parse(changed.Text);
        if (next.HasErrors)
        {
            var errors = next.Diagnostics.Where(d => d.IsError).ToList();
            throw Failed(i, "It would leave text Meeplang can't read: " + Describe(errors, next), errors);
        }
        var (registry, problems) = await _registryFor(next, cancellationToken).ConfigureAwait(false);
        _update = registry is null ? new DraftUpdate(false, null, problems, new Dictionary<string, string>()) : _draft.Preview(next, registry);
        var loaded = _update.Loaded ? _update.Shape : null;
        if (loaded is not null)
            Follow(i, loaded, next, changed);
        else
            Hold(i, changed);
        _text = next;
        _keyed = loaded;
    }

    // Edit i, with its module named as SourceEditor names it: a key or +N becomes the module's number now.
    private SourceEdit Target(int i)
    {
        var edit = _edits[i];
        if (edit.Kind >= EditKind.SetDefault)
            return edit;
        if (DraftEditor.Earlier(edit.Module) is { } earlier)
        {
            if (_keyed is null)
                throw Broken(i, $"the module edit {earlier + 1} added");
            return edit with { Module = AddedBy(earlier, i, _keyed) };
        }
        return ByKey(i) ?? throw Broken(i, $"the module with the key {edit.Module}");
    }

    // Follows the modules written so far into a text that loaded, then finds the ones edit i wrote.
    private void Follow(int i, PipelineShape loaded, PipelineSyntax next, EditedText changed)
    {
        var matched = ModuleMatcher.Match(_followed.Expanded, loaded.Expanded)
            .Where(m => m.Old is not null && m.Next is not null)
            .ToDictionary(m => m.Old!, m => m.Next!, s_sameLine);
        _tracked = [.. _tracked.Where(t => matched.ContainsKey(t.Module)).Select(t => t with { Module = matched[t.Module] })];
        var known = matched.Values.Concat(_tracked.Select(t => t.Module)).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var (edited, line) in _pending)
            _tracked.AddRange(loaded.Modules.Where(m => !known.Contains(m.Syntax) && LineOf(next, m.Syntax.Written) is { } written && written.Trim() == line).Select(m => new Tracked(edited, m.Syntax)));
        _pending.Clear();
        if (changed.AddedLine is { } added)
            _tracked.AddRange(loaded.Modules.Where(m => m.Syntax.Written.VerbSpan is { File: null } span && span.Line == added).Select(m => new Tracked(i, m.Syntax)));
        _followed = loaded;
    }

    // Edit i left text that doesn't load: remembers which edit broke it, and the line it wrote, if any, to find later.
    private void Hold(int i, EditedText changed)
    {
        if (_keyed is not null)
            _brokenBy = i;
        if (changed.AddedLine is { } added)
            _pending.Add(new Pending(i, changed.Text.Split('\n')[added - 1].Trim()));
    }

    // An edit that names its module by key, as SourceEditor names it: by its number in the text as it is now. Null
    // if it names one by key and the text doesn't load, so has no keys.
    private SourceEdit? ByKey(int index)
    {
        var edit = _edits[index];
        if (!DraftEditor.IsKey(edit.Module))
            return edit;
        if (_keyed?.Modules.FirstOrDefault(m => m.Key == edit.Module) is { } module)
            return edit with { Module = module.Info.Number.ToString(CultureInfo.InvariantCulture) };

        // A module may be called m3 with as; then the name names it.
        try
        {
            SourceEditor.Find(_text, edit.Module);
            return edit;
        }
        catch (EditException)
        {
        }
        if (_keyed is null)
            return null;
        var after = index == 0 ? "" : " after the edits before it";
        throw Failed(index, $"There's no module with the key {edit.Module}{after}. The keys are {string.Join(", ", _keyed.Modules.Select(m => $"{m.Key} ({m.Info})"))}.", []);
    }

    // The module an earlier edit added, as SourceEditor names it: by its number in the text as it is now. An edit that
    // writes a template's line adds a module for each use of the template; they're copies of one line, which an edit
    // to any of them changes, so the first names them all.
    private string AddedBy(int earlier, int index, PipelineShape keyed)
    {
        if (earlier >= index)
            throw Failed(index, $"It names +{earlier}, the module edit {earlier + 1} adds, but only an edit before it can add a module it names.", []);
        if (_edits[earlier].Kind is not (EditKind.Add or EditKind.Insert))
            throw Failed(index, $"It names +{earlier}, the module edit {earlier + 1} adds, but that edit ({_edits[earlier]}) doesn't add a module: only add and insert do.", []);
        var module = keyed.Modules.FirstOrDefault(m => _tracked.Any(t => t.Edit == earlier && ReferenceEquals(t.Module, m.Syntax)))
            ?? throw Failed(index, $"It names +{earlier}, the module edit {earlier + 1} added, but an edit after that took it out.", []);
        return module.Info.Number.ToString(CultureInfo.InvariantCulture);
    }

    // A module can't be found because an edit before it left text that doesn't load: that edit's to blame.
    private DraftEditException Broken(int index, string what)
    {
        var errors = _update!.Diagnostics.Where(d => d.IsError).ToList();
        return new DraftEditException(_brokenBy, $"{Which(_brokenBy)} leaves text that doesn't load: {Describe(errors, _text)} So {Which(index, capital: false)} can't find {what}. Name it by its name or number, or put the edits in an order that keeps the text loading. Nothing was changed.", errors);
    }

    // The text the edits made doesn't load, and which edit broke it, if one did.
    private DraftEditException DoesNotLoad()
    {
        var errors = _update!.Diagnostics.Where(d => d.IsError).ToList();
        var message = $"After the edits the text wouldn't load: {Describe(errors, _text)}";
        if (_brokenBy >= 0)
            message = $"{Which(_brokenBy)} leaves text that doesn't load, and no edit after it mends it: {Describe(errors, _text)}";
        return new DraftEditException(_brokenBy, message + " Nothing was changed.", errors);
    }

    private DraftEditException Failed(int index, string why, IReadOnlyList<Diagnostic> problems) =>
        new(index, $"{Which(index)} can't be made. {why} Nothing was changed.", problems);

    private string Which(int index, bool capital = true) =>
        (_edits.Count == 1 ? $"the edit ({_edits[index]})" : $"edit {index + 1} of {_edits.Count} ({_edits[index]})") is var which && capital
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

    // A module an edit wrote.
    private sealed record Tracked(int Edit, ModuleSyntax Module);

    // The line an edit wrote where the text didn't load, to find its module once it does.
    private sealed record Pending(int Edit, string Line);
}
