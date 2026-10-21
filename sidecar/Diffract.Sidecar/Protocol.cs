using Diffract.Git.History;
using Diffract.Git.Parsing;
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

/// <param name="Token">Chosen by the UI and sent back with every <c>history.page</c>, so it knows which read a page belongs to.</param>
internal sealed record HistoryReadParams(string? RepoId, string? Token);

internal sealed record HistoryRefsParams(string? RepoId);

internal sealed record HistoryDetailsParams(string? RepoId, string? CommitId);

/// <param name="BlobId">The <c>oldId</c> or <c>newId</c> of a file in <c>history.details</c>.</param>
internal sealed record HistoryFileParams(string? RepoId, string? BlobId);

/// <param name="Terminal">Null for the default: Windows Terminal when installed, otherwise Windows PowerShell.</param>
internal sealed record OpenTerminalParams(string? Path, TerminalKind? Terminal);

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

/// <summary>
/// The params of the <c>history.page</c> notification: the next commits of a <c>history.read</c>, in topological order.
/// Every page arrives before the read's own result.
/// </summary>
/// <param name="Index">0 for the first page, then 1, 2 and so on.</param>
internal sealed record HistoryPage(string Token, int Index, IReadOnlyList<CommitEntry> Commits);

/// <summary>The result of <c>history.read</c>, sent after its last page.</summary>
internal sealed record HistoryReadResult(int Commits, int Pages);

internal enum HistoryRefKind { Branch, RemoteBranch, Tag }

/// <summary>A ref history shows by default. Worktree HEADs are in the repo state.</summary>
/// <param name="Name">Short name: "feature/x", "origin/feature/x" or "v1.0".</param>
/// <param name="TargetId">The commit it points at, after peeling annotated tags.</param>
internal sealed record HistoryRef(string Name, HistoryRefKind Kind, string TargetId)
{
    public static IReadOnlyList<HistoryRef> From(RepoSnapshot s) =>
    [
        .. s.Branches.Select(b => new HistoryRef(b.Name, b.IsRemote ? HistoryRefKind.RemoteBranch : HistoryRefKind.Branch, b.TargetId)),
        .. s.Tags.Select(t => new HistoryRef(t.Name, HistoryRefKind.Tag, t.TargetId)),
    ];
}

/// <summary>The result of <c>history.file</c>: one side of a file's diff.</summary>
/// <param name="Text">The content, when <see cref="Kind"/> is Text.</param>
/// <param name="Message">Why there is no text to show, in plain English. Null for text.</param>
internal sealed record HistoryFile(FileContentKind Kind, string? Text, string? Message)
{
    public static HistoryFile From(FileContent content) => new(content.Kind, content.Text, content.Kind switch
    {
        FileContentKind.Binary => "This is a binary file, so there is no text to show.",
        FileContentKind.TooLarge => $"This file is larger than {HistoryReader.MaxFileBytes / (1024 * 1024)} MB, so Diffract does not show it.",
        _ => null,
    });
}
