namespace Meep2.Meeplang.Syntax;

/// <summary>
/// One module line, with the parameter lines and modules indented under it.
/// <c>[+] [Plugin.]Verb[.Inflection] [value] [name=value …] [as Name] [@modifier[=value] …]</c>
/// </summary>
public sealed class ModuleSyntax
{
    private readonly List<ArgumentSyntax> _arguments = [];
    private readonly List<ModifierSyntax> _modifiers = [];
    private readonly List<ModuleSyntax> _children = [];

    internal ModuleSyntax(bool isFeeder, string verb, SourceSpan verbSpan)
    {
        IsFeeder = isFeeder;
        Verb = verb;
        VerbSpan = verbSpan;
    }

    /// <summary>Whether the line starts with <c>+</c>, so it feeds the next unmarked line at the same indentation.</summary>
    public bool IsFeeder { get; }

    /// <summary>
    /// The verb, such as <c>Fetch</c>. In <c>Transit.Departures</c>, this is <c>Transit</c> and
    /// <see cref="Inflection"/> is <c>Departures</c>: whether that's a verb and inflection or a plugin and verb is only
    /// known once the plugins are loaded.
    /// </summary>
    public string Verb { get; }

    /// <summary>The plugin named in <c>Plugin.Verb.Inflection</c>, the one form that can only mean a plugin (Q105).</summary>
    public string? Plugin { get; internal set; }

    /// <summary>Where the verb is.</summary>
    public SourceSpan VerbSpan { get; }

    /// <summary>The inflection chosen with <c>Verb.Inflection</c>, if any.</summary>
    public string? Inflection { get; internal set; }

    /// <summary>The value written straight after the verb, if any.</summary>
    public ExpressionSyntax? Positional { get; internal set; }

    /// <summary>
    /// Where the value after the verb is written, brackets and all, which <see cref="ExpressionSyntax.Span"/> leaves out
    /// around a whole expression.
    /// </summary>
    public SourceSpan? PositionalSpan { get; internal set; }

    /// <summary>
    /// Where the module is written: from the start of its line (its <c>+</c>, or its verb) to the end of its last
    /// parameter line, or of its own line if it has none. Comments after it aren't included, and nor are the modules
    /// indented under it, unless parameter lines are written after them. For a copy, it's where the original is.
    /// </summary>
    public SourceRange Extent { get; internal set; }

    /// <summary>
    /// The template uses this module was copied in by, the one whose template it's written in first, then the use
    /// that one is in, and so on out to the pipeline's own modules. Empty if no template made it.
    /// </summary>
    public IReadOnlyList<ModuleSyntax> TemplateUses { get; internal set; } = [];

    /// <summary>Named parameters, from the module line and the parameter lines under it.</summary>
    public IReadOnlyList<ArgumentSyntax> Arguments => _arguments;

    /// <summary>The name given with <c>as Name</c>, if any.</summary>
    public string? Name { get; internal set; }

    /// <summary>Where the name is.</summary>
    public SourceSpan? NameSpan { get; internal set; }

    /// <summary>The <c>@</c> modifiers.</summary>
    public IReadOnlyList<ModifierSyntax> Modifiers => _modifiers;

    /// <summary>The modules indented under this one: each receives what this one emits, unless it's a <c>+</c> line.</summary>
    public IReadOnlyList<ModuleSyntax> Children => _children;

    internal void AddArgument(ArgumentSyntax argument) => _arguments.Add(argument);

    internal void AddModifier(ModifierSyntax modifier) => _modifiers.Add(modifier);

    internal void AddChild(ModuleSyntax child) => _children.Add(child);

    /// <summary>The template use this module was copied from, if a template made it.</summary>
    public ModuleSyntax? ExpandedFrom { get; internal set; }

    /// <summary>The module this is a copy of, if Distribute copied its branch (Q53). Copies share their original's name.</summary>
    public ModuleSyntax? CopyOf { get; internal set; }

    /// <summary>
    /// The module as written in its file, if this is a copy: made when templates were copied in, or when Distribute
    /// copied its branch. Edits to a copy change this line (Q129).
    /// </summary>
    public ModuleSyntax? Original { get; private set; }

    /// <summary>The module as written in its file: <see cref="Original"/>, or this one if it isn't a copy.</summary>
    public ModuleSyntax Written => Original ?? this;

    /// <summary>
    /// A copy of this module, with its expressions rewritten and other modules under it, as when a template is copied
    /// into a pipeline.
    /// </summary>
    internal ModuleSyntax Copy(Func<ExpressionSyntax, ExpressionSyntax> rewrite, IEnumerable<ModuleSyntax> children, bool? isFeeder = null)
    {
        var copy = new ModuleSyntax(isFeeder ?? IsFeeder, Verb, VerbSpan)
        {
            Plugin = Plugin,
            Inflection = Inflection,
            Positional = Positional is null ? null : rewrite(Positional),
            Name = Name,
            NameSpan = NameSpan,
            ExpandedFrom = ExpandedFrom,
            CopyOf = CopyOf,
            Original = Written,
            PositionalSpan = PositionalSpan,
            Extent = Extent,
            TemplateUses = TemplateUses,
        };
        foreach (var argument in _arguments)
            copy._arguments.Add(argument with { Value = rewrite(argument.Value) });
        foreach (var modifier in _modifiers)
            copy._modifiers.Add(modifier with { Value = modifier.Value is null ? null : rewrite(modifier.Value) });
        copy._children.AddRange(children);
        return copy;
    }

    /// <inheritdoc/>
    public override string ToString() => $"{(IsFeeder ? "+ " : "")}{(Plugin is null ? "" : Plugin + ".")}{Verb}{(Name is null ? "" : $" as {Name}")} (line {VerbSpan.Line})";
}
