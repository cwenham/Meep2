using Meep2.Meeplang.Editing;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Runtime;
using Meep2.SDK.Messages;

namespace Meep2.Daemon;

/// <summary>Describes a running pipeline as a graph, for <c>GET /pipelines/{name}/graph</c>.</summary>
public static class Graphs
{
    /// <summary>The graph of a pipeline that can be changed while it runs.</summary>
    /// <param name="name">The pipeline's name.</param>
    /// <param name="path">The file, or address, it came from.</param>
    /// <param name="live">The pipeline.</param>
    /// <param name="version">The graph's version, which the caller keeps.</param>
    public static PipelineGraph Describe(string name, string path, LivePipeline live, int version)
    {
        ArgumentNullException.ThrowIfNull(live);
        return Describe(name, path, live.Shape(), version);
    }

    /// <summary>The graph of a pipeline's shape.</summary>
    public static PipelineGraph Describe(string name, string path, PipelineShape shape, int version)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var files = new Files(shape.Syntax);
        var modules = shape.Modules.Select(module => Module(module, files)).ToList();
        var links = shape.Links.Select(link => new GraphLink(
            $"{link.From}>{link.To}",
            link.From,
            link.To,
            link.Input,
            link.Errors switch { ErrorDelivery.Only => "errors", ErrorDelivery.Exclude => "no-errors", _ => "main" },
            [.. link.Types.Where(t => t != typeof(Message)).Select(LinkShape.NameOf).Distinct().Order(StringComparer.Ordinal)])
        {
            Undeclared = link.Types.Contains(typeof(Message)),
        }).ToList();
        return new PipelineGraph(name, path, version, modules, links);
    }

    /// <summary>
    /// The counts for each link of a running pipeline, keyed as its graph's links are. A link whose modules have no key
    /// yet, in the moment while a change is made, is left out until the next time.
    /// </summary>
    /// <param name="live">The pipeline, for its modules' keys.</param>
    /// <param name="pipeline">The pipeline running now, or the last one to run, whose counts these are.</param>
    public static IReadOnlyList<LinkDetail> LinkCounts(LivePipeline live, Pipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(pipeline);
        var keys = live.ModuleKeys();
        var links = new List<LinkDetail>();
        foreach (var link in pipeline.GetLinkStats())
        {
            if (keys.TryGetValue(link.From, out var from) && keys.TryGetValue(link.To, out var to))
                links.Add(new LinkDetail($"{from}>{to}", link.Carried, link.Errors));
        }
        return links;
    }

    private static GraphModule Module(ModuleShape module, Files files)
    {
        var built = module.Syntax;
        var written = built.Written;

        // The parameters as written, where they're written, and as a template filled them in, if one did.
        var parameters = new List<GraphParameter>();
        if (written.Positional is { } positional)
        {
            var span = written.PositionalSpan ?? positional.Span;
            parameters.Add(new GraphParameter(null, files.Text(span), files.Span(span))
            {
                Value = Resolved(positional, built.Positional),
            });
        }
        for (var i = 0; i < written.Arguments.Count; i++)
        {
            var argument = written.Arguments[i];
            parameters.Add(new GraphParameter(argument.Name, ValueText(argument, files), files.Span(argument.Span ?? argument.NameSpan))
            {
                Value = i < built.Arguments.Count ? Resolved(argument.Value, built.Arguments[i].Value) : null,
            });
        }
        var modifiers = written.Modifiers
            .Select(m => new GraphModifier(m.Name, m.Value is null ? null : AfterEquals(files.Text(m.Span)), files.Span(m.Span)))
            .ToList();

        var file = written.Extent.File;
        return new GraphModule(
            module.Key,
            module.Info.Number,
            module.Info.ToString(),
            module.Info.Verb,
            module.Role.ToString().ToLowerInvariant(),
            [.. module.Inflections.Select(i => i.Inflection)],
            parameters,
            modifiers,
            files.Span(written.Extent))
        {
            Name = module.Info.Name,
            Plugin = module.Inflections[0].Plugin,
            Under = module.Under,
            Feeder = built.IsFeeder,
            CopyOf = module.CopyOf,
            Templates = built.TemplateUses.Count == 0 ? null : [.. built.TemplateUses.Select(use => new GraphTemplateUse(use.Verb, files.Span(use.Extent)))],
            Include = file is null ? null : files.IncludeOf(file),
        };
    }

    // A parameter's value as written, from the text after its name and =, or after by.
    private static string ValueText(ArgumentSyntax argument, Files files)
    {
        if (argument.Span is not { } span)
            return files.Text(argument.Value.Span);
        var text = files.Text(span);
        return argument.Name == "by" && !text.Contains('=', StringComparison.Ordinal) ? text[2..].TrimStart() : AfterEquals(text);
    }

    private static string AfterEquals(string text)
    {
        var equals = text.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? text : text[(equals + 1)..].TrimStart();
    }

    // A value as a template filled it in, if that's not as written.
    private static string? Resolved(ExpressionSyntax written, ExpressionSyntax? built) =>
        built is null || ReferenceEquals(written, built) || ExpressionText.Of(written) == ExpressionText.Of(built) ? null : ExpressionText.Of(built);

    // The pipeline's file and the files it includes, for the text and full path of a span.
    private sealed class Files(PipelineSyntax root)
    {
        private readonly Dictionary<string, PipelineSyntax> _included = root.Included.ToDictionary(i => i.Source.Path, StringComparer.Ordinal);

        public GraphSpan Span(SourceSpan span) => Span(SourceRange.Of(span));

        public GraphSpan Span(SourceRange range) =>
            new(range.File ?? root.Source.Path, range.StartLine, range.StartColumn, range.EndLine, range.EndColumn);

        public string Text(SourceSpan span) =>
            (span.File is { } file && _included.TryGetValue(file, out var included) ? included : root).TextOf(span);

        // The #include line that brought in a file: in the pipeline's own file, or in another included file.
        public GraphInclude? IncludeOf(string file)
        {
            foreach (var from in root.Included.Prepend(root))
            {
                foreach (var include in from.Includes)
                {
                    if (Resolves(include.Path, from.Source.Path, file))
                        return new GraphInclude(include.Path, Span(include.Span));
                }
            }
            return null;
        }

        // Whether an #include line names a file. Includes on this machine are found from the including file's
        // folder; those from a URL or Git are matched by the end of their path.
        private static bool Resolves(string included, string from, string file)
        {
            try
            {
                if (MeeplangParser.IncludePath(included, from) == file)
                    return true;
            }
            catch (ArgumentException)
            {
                // Not a path on this machine.
            }
            return file.EndsWith("/" + included.TrimStart('.', '/'), StringComparison.Ordinal);
        }
    }
}
