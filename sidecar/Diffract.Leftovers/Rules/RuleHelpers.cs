using Diffract.Git;
using Diffract.Git.Snapshot;

namespace Diffract.Leftovers.Rules;

/// <summary>Fills in the <see cref="Leftover.Agent"/> hint from the agent patterns. Never decides anything.</summary>
internal static class Attribution
{
    public static string? ForBranch(DiffractConfig config, string branch) =>
        config.Agents.FirstOrDefault(a => GlobMatcher.IsMatchAny(a.BranchPatterns, branch, ignoreCase: false))?.Name;

    public static string? ForPath(DiffractConfig config, string path) =>
        config.Agents.FirstOrDefault(a => GlobMatcher.IsMatchAny(a.WorktreePathPatterns, path, ignoreCase: true))?.Name;

    public static string? ForRef(DiffractConfig config, string fullName) =>
        config.Agents.FirstOrDefault(a => GlobMatcher.IsMatchAny(a.RefPatterns, fullName, ignoreCase: false))?.Name;

    public static string? ForCommit(DiffractConfig config, UnreachableCommit commit) =>
        config.Agents.FirstOrDefault(a => a.CommitSubjectPrefixes.Any(p => commit.Subject.StartsWith(p, StringComparison.Ordinal)))?.Name;

    /// <summary>The folder is the more reliable sign, since agents' branches get renamed.</summary>
    public static string? ForWorktree(DiffractConfig config, WorktreeInfo worktree) =>
        ForPath(config, worktree.Path) ?? (worktree.Branch is { } branch ? ForBranch(config, branch) : null);
}

/// <summary>Wording shared by the rules' reasons.</summary>
internal static class Describe
{
    public static string Age(TimeSpan age) =>
        age.TotalDays >= 1 ? Count((int)age.TotalDays, "day")
        : age.TotalHours >= 1 ? Count((int)age.TotalHours, "hour")
        : age.TotalMinutes >= 1 ? Count((int)age.TotalMinutes, "minute")
        : "less than a minute";

    public static string AtRisk(int commits) => commits switch
    {
        0 => "no commits exist only here",
        1 => "1 commit exists nowhere else",
        _ => $"{commits} commits exist nowhere else",
    };

    public static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}

internal static class Worktrees
{
    /// <summary>Linked worktrees whose folder still exists: the ones the worktree rules look at.</summary>
    public static IEnumerable<WorktreeInfo> Linked(RepoSnapshot repo) =>
        repo.Worktrees.Where(w => !w.IsMain && !w.IsBare && !w.IsPrunable);

    public static bool IsClean(WorktreeInfo worktree) => worktree.DirtyFileCount == 0;

    public static BranchInfo? Branch(RepoSnapshot repo, WorktreeInfo worktree) =>
        worktree.Branch is { } name ? repo.Branches.FirstOrDefault(b => !b.IsRemote && b.Name == name) : null;

    public static TimeSpan? Idle(WorktreeInfo worktree, TimeProvider time) =>
        worktree.LastActivityAt is { } at ? time.GetUtcNow() - at : null;

    public static Leftover Leftover(RepoSnapshot repo, DiffractConfig config, WorktreeInfo worktree, Verdict verdict, string reason) =>
        new(
            worktree.IsPrunable ? LeftoverKind.PrunableWorktree : LeftoverKind.Worktree,
            worktree.Branch ?? Path.GetFileName(worktree.Path),
            worktree.Path,
            Attribution.ForWorktree(config, worktree),
            verdict,
            Branch(repo, worktree)?.CommitsAtRisk ?? worktree.AheadOfMain ?? 0,
            reason);
}

internal static class Branches
{
    public static bool IsMergedOrGone(BranchInfo branch) => branch.IsMergedIntoMain || branch.IsUpstreamGone;

    /// <summary>Local branches other than main that no worktree has checked out, not even a prunable one.</summary>
    public static IEnumerable<BranchInfo> WithoutWorktree(RepoSnapshot repo) =>
        repo.Branches.Where(b => !b.IsRemote && b.Name != repo.MainBranch && !repo.Worktrees.Any(w => w.Branch == b.Name));

    public static Leftover Leftover(DiffractConfig config, BranchInfo branch, Verdict verdict, string reason) =>
        new(LeftoverKind.LocalBranch, branch.Name, null, Attribution.ForBranch(config, branch.Name), verdict, branch.CommitsAtRisk ?? 0, reason);
}
