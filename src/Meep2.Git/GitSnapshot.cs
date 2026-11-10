using System.Security.Cryptography;
using System.Text;
using LibGit2Sharp;

namespace Meep2.Git;

/// <summary>Why a pipeline couldn't be read from Git, in words.</summary>
public sealed class GitSourceException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// A repository at one commit, fetched into Meep's cache, for reading a pipeline and the files it includes from Git
/// (goal 7). Remotes are HTTPS only (Q66); a repository on this machine works too.
/// </summary>
public sealed class GitSnapshot
{
    private readonly string _folder;

    private GitSnapshot(string url, string folder, string commit, bool pinned)
    {
        Url = url;
        _folder = folder;
        Commit = commit;
        Pinned = pinned;
    }

    /// <summary>The repository's address.</summary>
    public string Url { get; }

    /// <summary>The commit read from.</summary>
    public string Commit { get; }

    /// <summary>
    /// Whether the address named the commit by its full SHA. A commit's SHA covers every file in it, so a pinned
    /// commit is trusted as a GitHub plugin pinned by its SHA is (Q102).
    /// </summary>
    public bool Pinned { get; }

    /// <summary>Fetches a repository into the cache, and finds the commit a ref names.</summary>
    /// <param name="url">An https:// address, or a repository on this machine (a path, or file://).</param>
    /// <param name="reference">A branch, a tag, or a commit's full SHA; null for main, or master.</param>
    /// <param name="cache">Meep's cache folder.</param>
    /// <exception cref="GitSourceException">The address isn't one Meep fetches, the fetch failed, or there's no such ref.</exception>
    public static GitSnapshot Fetch(string url, string? reference, string cache)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(cache);
        if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || url.Contains('@', StringComparison.Ordinal) && !url.Contains("://", StringComparison.Ordinal))
            throw new GitSourceException($"Meep reads Git over HTTPS only (Q66), so it can't fetch {url}. Use the repository's https:// address.");
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new GitSourceException($"Meep reads Git over HTTPS only, so it can't fetch {url}. Use https://.");

        var folder = Path.Combine(cache, "git", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16]);
        try
        {
            if (!Repository.IsValid(folder))
            {
                Directory.CreateDirectory(folder);
                Repository.Init(folder, isBare: true);
                using var created = new Repository(folder);
                created.Network.Remotes.Add("origin", url);
            }
            using var repository = new Repository(folder);
            Commands.Fetch(repository, "origin", ["+refs/heads/*:refs/remotes/origin/*", "+refs/tags/*:refs/tags/*"], new FetchOptions(), "meep: fetch");

            var pinned = reference is { Length: 40 } && reference.All(Uri.IsHexDigit);
            Commit? commit;
            if (pinned)
            {
                commit = repository.Lookup<Commit>(reference);
            }
            else
            {
                var names = reference is null ? ["main", "master"] : new[] { reference };
                commit = names
                    .Select(name => repository.Refs[$"refs/remotes/origin/{name}"] ?? repository.Refs[$"refs/tags/{name}"])
                    .Where(found => found is not null)
                    .Select(found => found!.ResolveToDirectReference().Target.Peel<Commit>())
                    .FirstOrDefault();
            }
            return commit is null
                ? throw new GitSourceException($"{url} has no {(reference is null ? "main or master branch" : $"branch, tag or commit {reference}")}.")
                : new GitSnapshot(url, folder, commit.Sha, pinned);
        }
        catch (LibGit2SharpException exception)
        {
            throw new GitSourceException($"Couldn't fetch {url}: {exception.Message}", exception);
        }
    }

    /// <summary>A file's text at this commit, or null if the commit has no such file.</summary>
    /// <param name="path">The file's path in the repository, with forward slashes.</param>
    public string? Read(string path) => ReadBytes(path) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>A file's bytes at this commit, or null if the commit has no such file.</summary>
    /// <param name="path">The file's path in the repository, with forward slashes.</param>
    public byte[]? ReadBytes(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var repository = new Repository(_folder);
        var commit = repository.Lookup<Commit>(Commit);
        if (commit?[path.TrimStart('/')]?.Target is not Blob blob)
            return null;
        using var content = blob.GetContentStream();
        using var bytes = new MemoryStream();
        content.CopyTo(bytes);
        return bytes.ToArray();
    }

    /// <summary>The commit's signature and the text it signs, or null if it isn't signed (Q134).</summary>
    public (string Signature, byte[] SignedData)? Signature()
    {
        using var repository = new Repository(_folder);
        try
        {
            var signed = LibGit2Sharp.Commit.ExtractSignature(repository, new ObjectId(Commit));
            return (signed.Signature, Encoding.UTF8.GetBytes(signed.SignedData));
        }
        catch (NotFoundException)
        {
            return null;
        }
    }
}
