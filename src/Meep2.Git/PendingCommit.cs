namespace Meep2.Git;

/// <summary>A commit write-back made: the branch, the commit and where the file is in the repository.</summary>
/// <param name="Branch">The branch it went on.</param>
/// <param name="Sha">The new commit's SHA.</param>
/// <param name="Repository">The repository's working folder.</param>
/// <param name="Path">The file's path within the repository, with forward slashes.</param>
public sealed record PendingCommit(string Branch, string Sha, string Repository, string Path)
{
    /// <summary>Why the commit isn't signed with your SSH key, if it isn't (Q126).</summary>
    public string? Unsigned { get; init; }
}
