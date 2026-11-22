using System.Text.Json;
using Meep2.Daemon;
using Meep2.Meeplang.Editing;
using Meep2.Meeplang.Loading;
using Meep2.Meeplang.Syntax;

namespace Meep2.Cli;

/// <summary>What <c>meep lsp</c> knows of an open file.</summary>
internal sealed class LspDocument(string uri, string path)
{
    public string Uri { get; } = uri;

    public string Path { get; } = path;

    public string Text { get; set; } = "";

    public int Version { get; set; }

    public PipelineSyntax? Syntax { get; set; }

    public ModuleRegistry? Registry { get; set; }

    public IReadOnlyList<Diagnostic> Problems { get; set; } = [];

    // Whether its text as it is now loaded.
    public bool Loaded { get; set; }

    public PipelineDraft Draft { get; } = new();

    public PipelineGraph? Graph { get; set; }

    public string? GraphJson { get; set; }

    public int GraphVersion { get; set; }

    public int? GraphFrom { get; set; }

    public string? GraphText { get; set; }

    public IReadOnlyDictionary<string, string> Renamed { get; set; } = new Dictionary<string, string>();

    public string? BaseName { get; set; }

    public int? BaseVersion { get; set; }

    public bool Subscribed { get; set; }

    public string? Sent { get; set; }

    public (string Id, int[] Data)? Tokens { get; set; }

    /// <summary>Keeps its new graph: its version counts up when anything in it changes, as the daemon's does.</summary>
    public void Record(PipelineShape shape, IReadOnlyDictionary<string, string> renamed, int builtFrom, string text)
    {
        var graph = GraphOf(shape);
        var json = JsonSerializer.Serialize(graph, DaemonJson.Default.PipelineGraph);
        if (json != GraphJson)
        {
            GraphJson = json;
            GraphVersion++;
            Renamed = renamed;
        }
        Graph = graph with { Version = GraphVersion };
        GraphFrom = builtFrom;
        GraphText = text;
    }

    /// <summary>A shape as the daemon's graph, named as the document's graph is, before it's given a version.</summary>
    public PipelineGraph GraphOf(PipelineShape shape) =>
        Graphs.Describe(BaseName ?? System.IO.Path.GetFileNameWithoutExtension(Path), Path, shape, version: 0);
}
