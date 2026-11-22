namespace Meep2.Meeplang.Syntax;

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

/// <summary>An <c>#include "file.meep"</c> line, which brings in another file's templates and constants.</summary>
/// <param name="Path">The file, as written: relative paths are from the including file's folder.</param>
/// <param name="Span">Where the line is.</param>
/// <param name="Sha256">
/// The SHA-256 the file must have, from <c>sha256:…</c> after it: a pin (Q136). A file trusted by a signature or a
/// pin then pins what it includes too, so one pin covers a pipeline and everything it includes.
/// </param>
public sealed record IncludeDirective(string Path, SourceSpan Span, string? Sha256 = null);
