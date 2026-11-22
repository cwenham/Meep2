using LibGit2Sharp;
using Meep2.Shared;

namespace Meep2.Git;

/// <summary>Why write-back couldn't commit, in words.</summary>
public sealed class PendingBranchException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Commits a pipeline file's new text to a branch, <c>pending</c> unless the edit names another, without touching the
/// working tree or the branch that's checked out (Q32). Merging it is up to you.
/// </summary>
/// <remarks>
/// The commit goes on top of the branch if it exists, and otherwise starts it from the commit that's checked out.
/// The branch can't be the one checked out, since moving it would leave the working tree behind it.
/// </remarks>
public static class PendingBranch
{
    /// <summary>The branch write-back commits to unless an edit names another.</summary>
    public const string Default = "pending";

    /// <summary>The working folder of the repository a file is in, or null if it isn't in one.</summary>
    public static string? RepositoryOf(string file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var found = Repository.Discover(Path.GetDirectoryName(Path.GetFullPath(file)));
        if (found is null)
            return null;
        using var repository = new Repository(found);
        return repository.Info.WorkingDirectory is { } folder ? Path.GetFullPath(folder) : null;
    }

    /// <summary>The file's text on a branch, or null if the branch, or the file on it, doesn't exist.</summary>
    public static string? Read(string file, string branch = Default)
    {
        ArgumentNullException.ThrowIfNull(file);
        using var repository = Open(file, out var path);
        var tip = repository.Branches[branch]?.Tip;
        return tip?[path]?.Target is Blob blob ? blob.GetContentText() : null;
    }

    /// <summary>Commits text as the file's content on a branch, signed with your SSH key where there is one (Q126).</summary>
    /// <param name="file">The pipeline file, on disk in a repository's working tree.</param>
    /// <param name="text">Its new text.</param>
    /// <param name="message">The commit message.</param>
    /// <param name="branch">The branch, <c>pending</c> unless given.</param>
    /// <param name="cancellationToken">Stops signing, which runs <c>ssh-keygen</c>; nothing is committed then.</param>
    /// <exception cref="PendingBranchException">The file isn't in a repository, the branch is checked out, or the repository has no commits to start from.</exception>
    public static async Task<PendingCommit> CommitAsync(string file, string text, string message, string branch = Default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        using var repository = Open(file, out var path);
        var tip = BranchTip.Find(repository, branch);
        try
        {
            var tree = TreeWith(repository, tip.Parent, path, text);
            var (id, unsigned) = await CreateCommitAsync(repository, tip.Parent, tree, message, cancellationToken).ConfigureAwait(false);
            tip.MoveTo(repository, id, message);
            return new PendingCommit(branch, id.Sha, Path.GetFullPath(repository.Info.WorkingDirectory), path) { Unsigned = unsigned };
        }
        catch (LibGit2SharpException exception)
        {
            throw new PendingBranchException($"Git couldn't commit to {branch}: {exception.Message}", exception);
        }
    }

    // The parent's tree with the file's new text in it.
    private static Tree TreeWith(Repository repository, Commit parent, string path, string text)
    {
        var blob = repository.ObjectDatabase.CreateBlob(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)));
        var tree = TreeDefinition.From(parent.Tree);
        tree.Add(path, blob, Mode.NonExecutableFile);
        return repository.ObjectDatabase.CreateTree(tree);
    }

    // The commit, signed with your SSH key where there is one; otherwise unsigned, saying why.
    private static async Task<(ObjectId Id, string? Unsigned)> CreateCommitAsync(Repository repository, Commit parent, Tree tree, string message, CancellationToken cancellationToken)
    {
        var signature = SignatureOf(repository);
        var key = CommitSigning.KeyFor(repository);
        var content = LibGit2Sharp.Commit.CreateBuffer(signature, signature, message, tree, [parent], prettifyMessage: true, commentChar: null);
        ValueOrWhyNot<string> signing;
        try
        {
            signing = await key.ThenAsync(found => CommitSigning.SignAsync(content, found, cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            CommitSigning.LetGo(key.Value);
        }
        return signing.TryGet(out var signed, out var whyNot)
            ? (repository.ObjectDatabase.CreateCommitWithSignature(content, signed), null)
            : (repository.ObjectDatabase.CreateCommit(signature, signature, message, tree, [parent], prettifyMessage: true).Id, whyNot);
    }

    /// <summary>
    /// Whether a commit is in the history of the branch that's checked out, as once you've merged <c>pending</c> into
    /// it. False if the file isn't in a repository, or the commit isn't known.
    /// </summary>
    public static bool IsMerged(string file, string sha)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(sha);
        if (RepositoryOf(file) is null)
            return false;
        using var repository = Open(file, out _);
        var commit = repository.Lookup<Commit>(sha);
        var head = repository.Head.Tip;
        if (commit is null || head is null)
            return false;
        return commit.Id == head.Id || repository.ObjectDatabase.FindMergeBase(commit, head)?.Id == commit.Id;
    }

    private static Repository Open(string file, out string path)
    {
        var full = Path.GetFullPath(file);
        var found = Repository.Discover(Path.GetDirectoryName(full))
            ?? throw new PendingBranchException($"{full} isn't in a Git repository, so there's no branch to commit the edit to.");
        var repository = new Repository(found);
        if (repository.Info.WorkingDirectory is null)
        {
            repository.Dispose();
            throw new PendingBranchException($"The repository at {found} has no working tree.");
        }
        // The working folder as the file's own path reaches it: Git reports it with links resolved, such as
        // /private/var for /var on macOS, which would make the file look outside it.
        var root = Path.GetDirectoryName(full);
        while (root is not null && !Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
            root = Path.GetDirectoryName(root);
        path = Path.GetRelativePath(root ?? repository.Info.WorkingDirectory, full).Replace(Path.DirectorySeparatorChar, '/');
        if (path.StartsWith("../", StringComparison.Ordinal))
        {
            repository.Dispose();
            throw new PendingBranchException($"{full} isn't inside the repository at {repository.Info.WorkingDirectory}.");
        }
        return repository;
    }

    // The repository's user, or Meep if it has none configured.
    private static Signature SignatureOf(Repository repository)
    {
        var name = repository.Config.Get<string>("user.name")?.Value;
        var email = repository.Config.Get<string>("user.email")?.Value;
        return new Signature(name ?? "Meep", email ?? "meep@localhost", DateTimeOffset.Now);
    }
}
