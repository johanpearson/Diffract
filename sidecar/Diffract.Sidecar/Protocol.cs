using Diffract.Git.Snapshot;
using Diffract.Leftovers;

namespace Diffract.Sidecar;

// Params and results of the sidecar's methods. They mirror ui/src/protocol.ts, and the contract
// samples in tests/Diffract.Sidecar.Tests/Contract keep the two in step.

internal sealed record RepoOpenParams(string? Path);

/// <param name="IncludeUnreachable">Also count unreachable commits with <c>git fsck</c>, the slowest call by far.</param>
internal sealed record RepoRefreshParams(string? RepoId, bool IncludeUnreachable);

internal sealed record RepoCloseParams(string? RepoId);

internal sealed record OpenFolderParams(string? Path);

/// <summary>What the UI shows for one open repo. The result of <c>repo.open</c> and <c>repo.refresh</c>.</summary>
/// <param name="RepoRoot">The main worktree, whatever path the repo was opened with.</param>
/// <param name="TakenAt">When this snapshot was taken.</param>
/// <param name="UnreachableCountedAt">When unreachable commits were last counted. Their rows are as old as this.</param>
internal sealed record RepoState(
    string RepoId,
    string RepoRoot,
    string MainBranch,
    DateTimeOffset TakenAt,
    DateTimeOffset UnreachableCountedAt,
    SnapshotSummary Snapshot,
    LeftoverSummary Summary,
    IReadOnlyList<Leftover> Leftovers)
{
    public static RepoState From(string repoId, RepoSnapshot s, IReadOnlyList<Leftover> leftovers, DateTimeOffset unreachableCountedAt) => new(
        RepoId: repoId,
        RepoRoot: s.RepoRoot,
        MainBranch: s.MainBranch,
        TakenAt: s.TakenAt,
        UnreachableCountedAt: unreachableCountedAt,
        Snapshot: new SnapshotSummary(
            Worktrees: s.Worktrees,
            LocalBranches: s.Branches.Count(b => !b.IsRemote),
            RemoteBranches: s.Branches.Count(b => b.IsRemote),
            Tags: s.Tags.Count,
            Stashes: s.Stashes.Count,
            OtherRefs: s.OtherRefs.Count,
            UnreachableCommits: s.UnreachableCommits.Count,
            LeftoverFolders: s.LeftoverFolders),
        Summary: new LeftoverSummary(
            Total: leftovers.Count,
            SafeToRemove: leftovers.Count(l => l.Verdict == Verdict.SafeToRemove),
            NeedsReview: leftovers.Count(l => l.Verdict == Verdict.NeedsReview),
            InUse: leftovers.Count(l => l.Verdict == Verdict.InUse)),
        Leftovers: leftovers);
}

/// <summary>Counts for refs, which can number in the hundreds; worktrees and leftover folders in full.</summary>
internal sealed record SnapshotSummary(
    IReadOnlyList<WorktreeInfo> Worktrees,
    int LocalBranches,
    int RemoteBranches,
    int Tags,
    int Stashes,
    int OtherRefs,
    int UnreachableCommits,
    IReadOnlyList<LeftoverFolderInfo> LeftoverFolders);

internal sealed record LeftoverSummary(int Total, int SafeToRemove, int NeedsReview, int InUse);

/// <summary>The params of the <c>repo.changed</c> notification. The UI replaces its state with <see cref="State"/>.</summary>
internal sealed record RepoChanged(IReadOnlyList<RepoChange> Changes, RepoState State);

/// <param name="Path">The worktree, for changes that belong to one. Null otherwise.</param>
internal sealed record RepoChange(ChangeKind Kind, string? Path)
{
    public static IReadOnlyList<RepoChange> From(ChangeSet changes)
    {
        if (changes.Everything)
            return [new RepoChange(ChangeKind.Everything, null)];

        List<RepoChange> list = [];
        if (changes.WorktreeList)
            list.Add(new RepoChange(ChangeKind.WorktreeList, null));
        if (changes.Refs)
            list.Add(new RepoChange(ChangeKind.Refs, null));
        list.AddRange(changes.WorktreeHeads.Order(StringComparer.OrdinalIgnoreCase).Select(p => new RepoChange(ChangeKind.WorktreeHead, p)));
        list.AddRange(changes.WorktreeFiles.Order(StringComparer.OrdinalIgnoreCase).Select(p => new RepoChange(ChangeKind.WorktreeFiles, p)));
        return list;
    }
}

/// <summary>What a change refreshed. One per row of the table in the milestone 2 doc, phase 3.</summary>
internal enum ChangeKind
{
    /// <summary>Files in one worktree.</summary>
    WorktreeFiles,

    /// <summary>One worktree's HEAD or index.</summary>
    WorktreeHead,

    /// <summary>Refs or packed-refs.</summary>
    Refs,

    /// <summary>Worktrees were added or removed, or a worktree container folder changed.</summary>
    WorktreeList,

    /// <summary>The watcher lost track and everything was rebuilt.</summary>
    Everything,
}
