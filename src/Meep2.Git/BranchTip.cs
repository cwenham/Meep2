using LibGit2Sharp;

namespace Meep2.Git;

/// <summary>
/// The branch write-back commits to, and the commit it goes on top of: the branch's tip if it exists, and otherwise
/// the commit that's checked out, which starts it.
/// </summary>
internal sealed record BranchTip(string Name, Branch? Existing, Commit Parent)
{
    /// <summary>The branch, checked as one write-back may commit to.</summary>
    /// <exception cref="PendingBranchException">Git doesn't allow the name, the branch is checked out, or the repository has no commits to start from.</exception>
    public static BranchTip Find(Repository repository, string branch)
    {
        if (!Reference.IsValidName("refs/heads/" + branch))
            throw new PendingBranchException($"{branch} isn't a name Git allows for a branch.");
        if (!repository.Info.IsHeadDetached && repository.Head.FriendlyName == branch)
        {
            throw new PendingBranchException(
                $"{branch} is checked out in {repository.Info.WorkingDirectory}, so committing to it would leave your working tree behind. Check out another branch, or name another for the edit.");
        }
        var existing = repository.Branches[branch];
        var parent = existing?.Tip ?? repository.Head.Tip
            ?? throw new PendingBranchException($"The repository at {repository.Info.WorkingDirectory} has no commits yet, so there's nothing for {branch} to start from.");
        return new BranchTip(branch, existing, parent);
    }

    /// <summary>Moves the branch to a new commit, or makes it there.</summary>
    public void MoveTo(Repository repository, ObjectId commit, string message)
    {
        if (Existing is null)
            repository.Branches.Add(Name, repository.Lookup<Commit>(commit));
        else
            repository.Refs.UpdateTarget(Existing.Reference, commit, $"meep: {message.Split('\n')[0]}");
    }
}
