namespace Meep2.Meeplang.Syntax;

/// <summary>A parsed Meeplang file.</summary>
public sealed class PipelineSyntax
{
    internal PipelineSyntax(
        SourceFile source,
        IReadOnlyList<ArgumentSyntax> defaults,
        IReadOnlyList<ConstantSyntax> constants,
        IReadOnlyList<ModuleSyntax> modules,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        Source = source;
        Defaults = defaults;
        Constants = constants;
        Modules = modules;
        Diagnostics = diagnostics;
    }

    /// <summary>The file this came from.</summary>
    public SourceFile Source { get; }

    /// <summary>Settings from the <c>defaults</c> block.</summary>
    public IReadOnlyList<ArgumentSyntax> Defaults { get; }

    /// <summary>The <c>const</c> declarations, in order.</summary>
    public IReadOnlyList<ConstantSyntax> Constants { get; }

    /// <summary>The modules written at the left margin, in order, including <c>+</c> lines.</summary>
    public IReadOnlyList<ModuleSyntax> Modules { get; }

    /// <summary>Problems found while parsing.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Whether parsing found any errors.</summary>
    public bool HasErrors => Diagnostics.Any(d => d.IsError);

    /// <summary>The source text a span covers.</summary>
    public string TextOf(SourceSpan span)
    {
        var lines = Source.Text.Split('\n');
        if (span.Line < 1 || span.Line > lines.Length)
            return "";
        var line = lines[span.Line - 1].TrimEnd('\r');
        var start = Math.Clamp(span.Column - 1, 0, line.Length);
        var length = Math.Clamp(span.Length, 0, line.Length - start);
        return line.Substring(start, length);
    }
}

/// <summary>
/// One module line, with the parameter lines and modules indented under it.
/// <c>[+] Verb[.Inflection] [value] [name=value …] [as Name] [@modifier[=value] …]</c>
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

    /// <summary>The verb, such as <c>Fetch</c>.</summary>
    public string Verb { get; }

    /// <summary>Where the verb is.</summary>
    public SourceSpan VerbSpan { get; }

    /// <summary>The inflection chosen with <c>Verb.Inflection</c>, if any.</summary>
    public string? Inflection { get; internal set; }

    /// <summary>The value written straight after the verb, if any.</summary>
    public ExpressionSyntax? Positional { get; internal set; }

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

    /// <inheritdoc/>
    public override string ToString() => $"{(IsFeeder ? "+ " : "")}{Verb}{(Name is null ? "" : $" as {Name}")} (line {VerbSpan.Line})";
}

/// <summary>A named value: a parameter (<c>timeout = 5s</c> or <c>timeout=5s</c>) or a setting under <c>defaults</c>.</summary>
/// <param name="Name">The name as written.</param>
/// <param name="Value">The value.</param>
/// <param name="NameSpan">Where the name is.</param>
public sealed record ArgumentSyntax(string Name, ExpressionSyntax Value, SourceSpan NameSpan);

/// <summary>An <c>@</c> modifier, such as <c>@bypass</c> or <c>@buffer=1000</c>.</summary>
/// <param name="Name">The name, without the <c>@</c>.</param>
/// <param name="Value">The value, if one was given.</param>
/// <param name="Span">Where the modifier is.</param>
public sealed record ModifierSyntax(string Name, ExpressionSyntax? Value, SourceSpan Span);

/// <summary>A <c>const Name = value</c> declaration.</summary>
/// <param name="Name">The constant's name.</param>
/// <param name="Value">Its value.</param>
/// <param name="NameSpan">Where the name is.</param>
public sealed record ConstantSyntax(string Name, ExpressionSyntax Value, SourceSpan NameSpan);
