using Diffract.Git.Snapshot;

namespace Diffract.Leftovers.Cleanup;

/// <summary>
/// Plan, then apply, for "Delete branch" in the history menu: one local branch the user picked, whether or not it is
/// a leftover. It is planned like any cleanup, so it gets the same backup tag for work that exists nowhere else, the
/// same confirm dialog and the same undo. A pure function over the snapshot.
/// </summary>
public static class BranchPlanner
{
    /// <summary>
    /// The plan for the local branch <paramref name="name"/>. A branch checked out in any worktree is refused, and so is
    /// one the snapshot does not have, or one whose commits at risk are unknown, since nothing then says whether a
    /// backup is needed.
    /// </summary>
    public static CleanupPlan Plan(RepoSnapshot repo, string name, DiffractConfig config, TimeProvider time) =>
        CleanupPlanner.Plan(repo, [Leftover(repo, name)], config, time);

    /// <summary>The worktree that has the branch checked out, or null.</summary>
    public static WorktreeInfo? CheckedOutIn(RepoSnapshot repo, string name) =>
        repo.Worktrees.FirstOrDefault(w => w.Branch == name);

    private static Leftover Leftover(RepoSnapshot repo, string name)
    {
        var branch = repo.Branches.FirstOrDefault(b => !b.IsRemote && b.Name == name);
        if (CheckedOutIn(repo, name) is { } worktree)
            return Of(name, branch, Verdict.InUse, $"Checked out in {Path.GetFileName(Path.TrimEndingDirectorySeparator(worktree.Path))}.");
        if (branch is null)
            return Of(name, null, Verdict.NeedsReview, "It is no longer there.");
        return branch.CommitsAtRisk is null
            ? Of(name, branch, Verdict.InUse, "Diffract could not tell whether it holds work that exists nowhere else.")
            : Of(name, branch, branch.IsMergedIntoMain || branch.IsUpstreamGone ? Verdict.SafeToRemove : Verdict.NeedsReview, "Picked in the history.");
    }

    private static Leftover Of(string name, BranchInfo? branch, Verdict verdict, string reason) =>
        new(LeftoverKind.LocalBranch, name, null, null, verdict, branch?.CommitsAtRisk, reason) { Commit = branch?.TargetId };
}
