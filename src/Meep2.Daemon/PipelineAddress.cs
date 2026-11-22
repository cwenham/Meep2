namespace Meep2.Daemon;

/// <summary>A pipeline's address, as <c>meep run</c> and <c>meep start</c> take it.</summary>
/// <param name="Origin">Where it comes from.</param>
/// <param name="Location">The file's full path, the URL, or the repository's address.</param>
/// <param name="Path">For Git, the file's path in the repository.</param>
/// <param name="Reference">For Git, the branch, tag or commit; null for main or master.</param>
/// <param name="Sha256">For a URL, the SHA-256 its text must have, from <c>#sha256=…</c>: a pin, trusted without a signature.</param>
public sealed record PipelineAddress(PipelineOrigin Origin, string Location, string? Path = null, string? Reference = null, string? Sha256 = null)
{
    /// <summary>Reads an address: a file, <c>-</c>, a URL, or <c>git+</c> and a repository, <c>#</c> and a path, then <c>@</c> and a ref.</summary>
    /// <exception cref="FormatException">A git+ address without a file.</exception>
    public static PipelineAddress Parse(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address == "-")
            return new PipelineAddress(PipelineOrigin.Stdin, "stdin");
        if (address.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
        {
            var rest = address[4..];
            var hash = rest.LastIndexOf('#');
            if (hash < 0 || hash == rest.Length - 1)
                throw new FormatException($"A pipeline in Git is written as git+https://host/owner/repo.git#path/file.meep@ref, with the file after #. {address} has none.");
            var file = rest[(hash + 1)..];
            string? reference = null;
            var at = file.LastIndexOf('@');
            if (at > 0)
            {
                reference = file[(at + 1)..];
                file = file[..at];
            }
            var repository = rest[..hash];
            if (Uri.TryCreate(repository, UriKind.Absolute, out var uri) && uri.IsFile)
                repository = uri.LocalPath;
            return new PipelineAddress(PipelineOrigin.Git, repository, file.TrimStart('/'), reference);
        }
        if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var hash = address.IndexOf("#sha256=", StringComparison.OrdinalIgnoreCase);
            return hash < 0
                ? new PipelineAddress(PipelineOrigin.Url, address)
                : new PipelineAddress(PipelineOrigin.Url, address[..hash], Sha256: address[(hash + "#sha256=".Length)..].ToLowerInvariant());
        }
        return new PipelineAddress(PipelineOrigin.File, System.IO.Path.GetFullPath(address));
    }

    /// <summary>The pipeline's name: its file's name without <c>.meep</c>.</summary>
    public string Name => Origin switch
    {
        PipelineOrigin.Stdin => "stdin",
        PipelineOrigin.Git => System.IO.Path.GetFileNameWithoutExtension(Path!),
        PipelineOrigin.Url => System.IO.Path.GetFileNameWithoutExtension(new Uri(Location).AbsolutePath) is { Length: > 0 } name ? name : "pipeline",
        _ => System.IO.Path.GetFileNameWithoutExtension(Location),
    };

    /// <summary>Whether it's a file on this machine, which edits can be saved to and which can be watched.</summary>
    public bool IsFile => Origin == PipelineOrigin.File;

    /// <summary>The address as it's written.</summary>
    public override string ToString() => Origin switch
    {
        PipelineOrigin.Stdin => "-",
        PipelineOrigin.Git => $"git+{Location}#{Path}{(Reference is null ? "" : "@" + Reference)}",
        PipelineOrigin.Url => Sha256 is null ? Location : $"{Location}#sha256={Sha256}",
        _ => Location,
    };
}
