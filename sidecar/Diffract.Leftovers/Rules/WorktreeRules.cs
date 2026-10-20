using Diffract.Git.Snapshot;

namespace Diffract.Leftovers.Rules;

/// <summary>Anything changed within <c>activeWithinMinutes</c> is in use.</summary>
public sealed class RecentActivityRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time) =>
        from w in Worktrees.Linked(repo)
        let idle = Worktrees.Idle(w, time)
        where idle <= TimeSpan.FromMinutes(config.ActiveWithinMinutes)
        select Worktrees.Leftover(repo, config, w, Verdict.InUse, $"Files changed {Describe.Age(idle.Value)} ago");
}

/// <summary>A locked worktree is in use, whatever its age.</summary>
public sealed class LockedWorktreeRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time) =>
        from w in Worktrees.Linked(repo)
        where w.IsLocked
        select Worktrees.Leftover(repo, config, w, Verdict.InUse,
            string.IsNullOrWhiteSpace(w.LockReason) ? "Locked in git" : $"Locked in git: {w.LockReason}");
}

/// <summary>Uncommitted changes mean someone is working there, whatever their age.</summary>
public sealed class DirtyWorktreeRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time) =>
        from w in Worktrees.Linked(repo)
        where w.DirtyFileCount > 0
        select Worktrees.Leftover(repo, config, w, Verdict.InUse, $"{Describe.Count(w.DirtyFileCount!.Value, "uncommitted change")}");
}

/// <summary>The folder was deleted without telling git. Removing the record loses nothing; the branch stays.</summary>
public sealed class PrunableWorktreeRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time) =>
        from w in repo.Worktrees
        where w.IsPrunable && !w.IsMain
        select Worktrees.Leftover(repo, config, w, Verdict.SafeToRemove, "Folder was deleted outside git") with { CommitsAtRisk = 0 };
}

/// <summary>A clean worktree on a branch that is merged, or whose upstream is gone after a squash merge.</summary>
public sealed class MergedWorktreeRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time)
    {
        foreach (var w in Worktrees.Linked(repo))
        {
            // A linked worktree on main itself is somebody's working copy, not a finished branch.
            if (!Worktrees.IsClean(w) || w.Branch == repo.MainBranch || Worktrees.Branch(repo, w) is not { } branch || !Branches.IsMergedOrGone(branch))
                continue;

            var reason = branch.IsMergedIntoMain
                ? $"Branch merged into {repo.MainBranch}, worktree is clean"
                : $"Upstream branch was deleted, probably merged; worktree is clean and {Describe.AtRisk(branch.CommitsAtRisk ?? 0)}";
            yield return Worktrees.Leftover(repo, config, w, Verdict.SafeToRemove, reason);
        }
    }
}

/// <summary>A clean worktree on an unmerged branch with no activity for <c>staleAfterDays</c>.</summary>
public sealed class StaleWorktreeRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time)
    {
        foreach (var w in Worktrees.Linked(repo))
        {
            if (!Worktrees.IsClean(w) || Worktrees.Branch(repo, w) is not { } branch || Branches.IsMergedOrGone(branch))
                continue;
            if (Worktrees.Idle(w, time) is not { } idle || idle < TimeSpan.FromDays(config.StaleAfterDays))
                continue;

            yield return Worktrees.Leftover(repo, config, w, Verdict.NeedsReview,
                $"No activity for {Describe.Age(idle)}, {Describe.AtRisk(branch.CommitsAtRisk ?? 0)}");
        }
    }
}

/// <summary>A clean worktree with a detached HEAD has no branch to say whether it is finished.</summary>
public sealed class DetachedWorktreeRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time)
    {
        foreach (var w in Worktrees.Linked(repo))
        {
            if (!w.IsDetached || !Worktrees.IsClean(w))
                continue;

            var ahead = w.AheadOfMain ?? 0;
            var reason = ahead == 0
                ? $"Detached HEAD at a commit already on {repo.MainBranch}"
                : $"Detached HEAD with {Describe.Count(ahead, "commit")} not on {repo.MainBranch}";
            yield return Worktrees.Leftover(repo, config, w, Verdict.NeedsReview, reason);
        }
    }
}
