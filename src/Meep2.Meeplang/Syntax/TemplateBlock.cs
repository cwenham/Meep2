namespace Meep2.Meeplang.Syntax;

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
