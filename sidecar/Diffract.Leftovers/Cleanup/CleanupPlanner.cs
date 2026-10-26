using Diffract.Git.Snapshot;
using Diffract.Leftovers.Rules;

namespace Diffract.Leftovers.Cleanup;

/// <summary>
/// Plan, then apply: turns the leftovers the user selected into the steps that remove them, from the snapshot they
/// were found in. A pure function: no git calls, no file system. Nothing "In use" is ever planned, and neither is a
/// leftover the snapshot no longer has.
/// </summary>
public static class CleanupPlanner
{
    /// <summary>What every kind's planner reads.</summary>
    private sealed record Context(RepoSnapshot Repo, DiffractConfig Config, BackupTags Backups);

    /// <summary>The steps for one kind, or null when the snapshot no longer has the leftover.</summary>
    private delegate IReadOnlyList<CleanupStep>? KindPlanner(Context context, Leftover leftover);

    private static readonly Dictionary<LeftoverKind, KindPlanner> Planners = new()
    {
        [LeftoverKind.Worktree] = Worktree,
        [LeftoverKind.PrunableWorktree] = PrunableWorktree,
        [LeftoverKind.LeftoverFolder] = LeftoverFolder,
        [LeftoverKind.LocalBranch] = LocalBranch,
        [LeftoverKind.RemoteBranch] = RemoteBranch,
        [LeftoverKind.Stash] = Stash,
        [LeftoverKind.CheckpointSession] = CheckpointSession,
        [LeftoverKind.BackupTag] = BackupTag,
    };

    public static CleanupPlan Plan(RepoSnapshot repo, IReadOnlyList<Leftover> selected, DiffractConfig config, TimeProvider time)
    {
        var context = new Context(repo, config, new BackupTags(repo, time.GetUtcNow()));
        return new CleanupPlan([.. selected.Select(leftover => PlanOne(context, leftover))]);
    }

    private static LeftoverPlan PlanOne(Context context, Leftover leftover)
    {
        if (leftover.Verdict == Verdict.InUse)
            return LeftoverPlan.Refuse(leftover, "In use, so it is never cleaned up.");
        if (!Planners.TryGetValue(leftover.Kind, out var planner))
            return LeftoverPlan.Refuse(leftover, "Keeping orphaned commits comes later in this milestone, in phase 5.");
        return planner(context, leftover) is { } steps
            ? new LeftoverPlan(leftover, steps)
            : LeftoverPlan.Refuse(leftover, "It is no longer there. Refresh to see the repo as it is now.");
    }

    private static IReadOnlyList<CleanupStep>? Worktree(Context c, Leftover leftover) =>
        WorktreeAt(c.Repo, leftover) is { } worktree ? WorktreeSteps.Worktree(worktree, leftover.CommitsAtRisk, c.Backups) : null;

    private static IReadOnlyList<CleanupStep>? PrunableWorktree(Context c, Leftover leftover) =>
        WorktreeAt(c.Repo, leftover) is { } worktree ? WorktreeSteps.Prunable(worktree, leftover.CommitsAtRisk, c.Backups) : null;

    private static IReadOnlyList<CleanupStep>? LeftoverFolder(Context c, Leftover leftover) =>
        c.Repo.LeftoverFolders.FirstOrDefault(f => Same(f.Path, leftover.Path)) is { } folder ? WorktreeSteps.Folder(folder.Path) : null;

    private static IReadOnlyList<CleanupStep>? LocalBranch(Context c, Leftover leftover) =>
        Branch(c.Repo, leftover, remote: false) is { } branch ? RefSteps.LocalBranch(branch, c.Backups) : null;

    private static IReadOnlyList<CleanupStep>? RemoteBranch(Context c, Leftover leftover) =>
        Branch(c.Repo, leftover, remote: true) is { } branch ? RefSteps.RemoteBranch(branch, c.Backups) : null;

    private static IReadOnlyList<CleanupStep>? Stash(Context c, Leftover leftover) =>
        c.Repo.Stashes.FirstOrDefault(s => s.Name == leftover.Name) is { } stash ? RefSteps.Stash(stash, c.Backups) : null;

    /// <summary>The session's refs: those whose session, as the rule groups them, ends in the leftover's name.</summary>
    private static IReadOnlyList<CleanupStep>? CheckpointSession(Context c, Leftover leftover)
    {
        List<RefInfo> refs = [.. c.Repo.OtherRefs.Where(r => CheckpointSessionRule.SessionOf(c.Config, r.FullName)?.EndsWith($"/{leftover.Name}", StringComparison.Ordinal) == true)];
        return refs.Count > 0 ? RefSteps.CheckpointSession(leftover.Name, refs) : null;
    }

    private static IReadOnlyList<CleanupStep>? BackupTag(Context c, Leftover leftover) =>
        c.Repo.Tags.FirstOrDefault(t => t.Name == leftover.Name) is { } tag ? RefSteps.OldBackupTag(tag) : null;

    private static WorktreeInfo? WorktreeAt(RepoSnapshot repo, Leftover leftover) =>
        repo.Worktrees.FirstOrDefault(w => !w.IsMain && Same(w.Path, leftover.Path));

    private static BranchInfo? Branch(RepoSnapshot repo, Leftover leftover, bool remote) =>
        repo.Branches.FirstOrDefault(b => b.IsRemote == remote && b.Name == leftover.Name);

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
}
