namespace Meep2.Meeplang.Syntax;

/// <summary>A parsed Meeplang file.</summary>
public sealed class PipelineSyntax
{
    private readonly List<Diagnostic> _diagnostics;
    private readonly List<PipelineSyntax> _included = [];

    internal PipelineSyntax(
        SourceFile source,
        IReadOnlyList<ArgumentSyntax> defaults,
        IReadOnlyList<ConstantSyntax> constants,
        IReadOnlyList<PluginDirective> plugins,
        IReadOnlyList<ModuleSyntax> modules,
        IReadOnlyList<Diagnostic> diagnostics,
        IReadOnlyList<TemplateBlock>? templates = null,
        IReadOnlyList<IncludeDirective>? includes = null)
    {
        Source = source;
        Defaults = defaults;
        Constants = constants;
        Plugins = plugins;
        Modules = modules;
        _diagnostics = [.. diagnostics];
        Templates = templates ?? [];
        Includes = includes ?? [];
    }

    /// <summary>The file this came from.</summary>
    public SourceFile Source { get; }

    /// <summary>Settings from the <c>defaults</c> block.</summary>
    public IReadOnlyList<ArgumentSyntax> Defaults { get; }

    /// <summary>The <c>const</c> declarations, in order.</summary>
    public IReadOnlyList<ConstantSyntax> Constants { get; }

    /// <summary>The <c>#plugin</c> lines, in order.</summary>
    public IReadOnlyList<PluginDirective> Plugins { get; }

    /// <summary>The modules written at the left margin, in order, including <c>+</c> lines.</summary>
    public IReadOnlyList<ModuleSyntax> Modules { get; }

    /// <summary>The <c>template</c> blocks, in order.</summary>
    public IReadOnlyList<TemplateBlock> Templates { get; }

    /// <summary>The <c>#include</c> lines, in order.</summary>
    public IReadOnlyList<IncludeDirective> Includes { get; }

    /// <summary>The files <c>#include</c> brought in, and those they brought in, parsed, in the order they were met.</summary>
    public IReadOnlyList<PipelineSyntax> Included => _included;

    /// <summary>Problems found while parsing, the included files' among them.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>Whether parsing found any errors.</summary>
    public bool HasErrors => _diagnostics.Any(d => d.IsError);

    internal void AddIncluded(PipelineSyntax included)
    {
        _included.Add(included);
        _diagnostics.AddRange(included._diagnostics);
    }

    internal void AddDiagnostic(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    /// <summary>
    /// This file with other modules in place of its own: the modules once templates are copied in, which is what's
    /// built and matched.
    /// </summary>
    internal PipelineSyntax WithModules(IReadOnlyList<ModuleSyntax> modules)
    {
        var copy = new PipelineSyntax(Source, Defaults, Constants, Plugins, modules, [], Templates, Includes);
        copy._diagnostics.AddRange(_diagnostics);
        copy._included.AddRange(_included);
        return copy;
    }

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

/// <summary>Where a plugin comes from.</summary>
public enum PluginOrigin
{
    /// <summary>A release on GitHub: <c>#plugin github.com/owner/repo 1.2.0 sha256:…</c>.</summary>
    GitHub,

    /// <summary>A NuGet package: <c>#plugin nuget:Meep.Transit 1.2.0</c>.</summary>
    NuGet,

    /// <summary>A plugin on this machine, by path: <c>#plugin "../Transit/bin/Debug/net11.0/Transit.dll"</c>.</summary>
    Local,
}

/// <summary>
/// A <c>#plugin</c> line, naming a plugin the pipeline needs:
/// <c>#plugin github.com/owner/repo 1.2.0 sha256:…</c>, <c>#plugin nuget:Id 1.2.0 [sha256:…]</c>, or
/// <c>#plugin "path"</c> for one on this machine, each optionally followed by <c>as Name</c>.
/// </summary>
/// <param name="Origin">Where it comes from.</param>
/// <param name="Name"><c>owner/repo</c> on GitHub, the package ID on NuGet, or the path of a local plugin as written.</param>
/// <param name="Version">The version, or null for a local plugin.</param>
/// <param name="Sha256">The SHA-256 of the release asset or package, in lowercase hex, if given.</param>
/// <param name="Span">Where the line is, for diagnostics.</param>
public sealed record PluginDirective(PluginOrigin Origin, string Name, string? Version, string? Sha256, SourceSpan Span)
{
    /// <summary>
    /// The name the line gives the plugin with <c>as Name</c>, for reaching its verbs as <c>Name.Verb</c> (Q105).
    /// Without it, the plugin is named after its assembly.
    /// </summary>
    public string? Alias { get; init; }

    /// <summary>The plugin as it's written after <c>#plugin</c>, without the SHA.</summary>
    public override string ToString() => Origin switch
    {
        PluginOrigin.GitHub => $"github.com/{Name} {Version}",
        PluginOrigin.NuGet => $"nuget:{Name} {Version}",
        _ => Name,
    };
}

/// <summary>
/// A <c>template Name(Parameter, Parameter = default)</c> block: modules that a module line naming the template is
/// replaced by, before the pipeline is built, with its parameters' values in place of their names.
/// </summary>
public sealed class TemplateBlock
{
    private readonly List<ModuleSyntax> _body = [];

    internal TemplateBlock(string name, SourceSpan nameSpan, IReadOnlyList<TemplateParameter> parameters, string path)
    {
        Name = name;
        NameSpan = nameSpan;
        Parameters = parameters;
        Path = path;
    }

    /// <summary>The template's name, which module lines use as if it were a verb.</summary>
    public string Name { get; }

    /// <summary>Where the name is.</summary>
    public SourceSpan NameSpan { get; }

    /// <summary>Its parameters, in order. The first takes a use's main value.</summary>
    public IReadOnlyList<TemplateParameter> Parameters { get; }

    /// <summary>The modules indented under it.</summary>
    public IReadOnlyList<ModuleSyntax> Body => _body;

    /// <summary>The file it's written in.</summary>
    public string Path { get; }

    internal void AddModule(ModuleSyntax module) => _body.Add(module);
}

/// <summary>A template's parameter.</summary>
/// <param name="Name">Its name, which the template's expressions use.</param>
/// <param name="Default">Its value when a use doesn't give one, or null if every use must.</param>
/// <param name="Span">Where it's named.</param>
public sealed record TemplateParameter(string Name, ExpressionSyntax? Default, SourceSpan Span);

/// <summary>An <c>#include "file.meep"</c> line, which brings in another file's templates and constants.</summary>
/// <param name="Path">The file, as written: relative paths are from the including file's folder.</param>
/// <param name="Span">Where the line is.</param>
/// <param name="Sha256">
/// The SHA-256 the file must have, from <c>sha256:…</c> after it: a pin (Q136). A file trusted by a signature or a
/// pin then pins what it includes too, so one pin covers a pipeline and everything it includes.
/// </param>
public sealed record IncludeDirective(string Path, SourceSpan Span, string? Sha256 = null);
