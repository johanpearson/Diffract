using Diffract.Git.Changes;
using Diffract.Git.Files;
using Diffract.Git.History;
using Diffract.Git.Parsing;
using Diffract.Git.Refs;
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
/// <param name="Checkpoints">Also start from agents' checkpoint refs.</param>
/// <param name="Stashes">Also start from every stash. The scan after opening lists them, so before it there are none.</param>
internal sealed record HistoryReadParams(string? RepoId, string? Token, bool Checkpoints, bool Stashes);

/// <param name="Tips">The <c>tips</c> of the history the UI has: from <c>history.read</c>, or from the last <c>history.update</c>.</param>
internal sealed record HistoryUpdateParams(string? RepoId, IReadOnlyList<string>? Tips, bool Checkpoints, bool Stashes);

internal sealed record HistoryRefsParams(string? RepoId);

internal sealed record HistoryDetailsParams(string? RepoId, string? CommitId);

/// <param name="BlobId">The <c>oldId</c> or <c>newId</c> of a file in <c>history.details</c>.</param>
internal sealed record HistoryFileParams(string? RepoId, string? BlobId);

/// <param name="Worktree">The full path of one of the repo's worktrees, as the repo state lists them.</param>
internal sealed record ChangesStatusParams(string? RepoId, string? Worktree);

/// <summary>The params of <c>changes.file</c>: the file as <c>changes.status</c> listed it.</summary>
internal sealed record ChangesFileParams
{
    public string? RepoId { get; init; }

    public string? Worktree { get; init; }

    /// <summary>The file's <c>path</c> from <c>changes.status</c>.</summary>
    public string? Path { get; init; }

    /// <summary>The file's <c>oldPath</c>, so a staged rename's hunks are read as one diff.</summary>
    public string? OldPath { get; init; }

    /// <summary>HEAD against the index when true, the index against the worktree when false.</summary>
    public bool Staged { get; init; }

    /// <summary>The file's <c>headId</c> from <c>changes.status</c>.</summary>
    public string? HeadId { get; init; }

    /// <summary>The file's <c>indexId</c> from <c>changes.status</c>.</summary>
    public string? IndexId { get; init; }
}

/// <summary>The params of <c>changes.stage</c> and <c>changes.unstage</c>: whole files, or one hunk. Never both.</summary>
internal sealed record ChangesStageParams
{
    public string? RepoId { get; init; }

    public string? Worktree { get; init; }

    /// <summary>Files to stage or unstage, with one git call. For a staged rename, both paths.</summary>
    public IReadOnlyList<string>? Paths { get; init; }

    /// <summary>One hunk of the unstaged diff to stage, or of the staged diff to unstage.</summary>
    public ChangesHunkParams? Hunk { get; init; }
}

/// <summary>The params of <c>changes.commit</c>.</summary>
internal sealed record ChangesCommitParams
{
    public string? RepoId { get; init; }

    public string? Worktree { get; init; }

    /// <summary>The subject, then optionally a blank line and a body, exactly as typed.</summary>
    public string? Message { get; init; }

    /// <summary>Replace the last commit. Refused when it is already pushed.</summary>
    public bool Amend { get; init; }
}

/// <summary>The result of <c>changes.commit</c>.</summary>
/// <param name="CommitId">The new commit.</param>
/// <param name="Status">The worktree after the commit.</param>
internal sealed record ChangesCommitResult(string CommitId, WorktreeChanges Status);

/// <summary>The result of <c>remote.fetch</c>, <c>remote.pull</c> and <c>remote.push</c>, when git did not fail.</summary>
/// <param name="Message">What happened, in plain words.</param>
/// <param name="Status">The worktree afterwards, with new ahead and behind counts.</param>
internal sealed record RemoteSyncResult(RemoteOutcome Outcome, string Message, WorktreeChanges Status);

/// <summary>The params of <c>ref.checkName</c>.</summary>
internal sealed record RefCheckNameParams(string? RepoId, string? Worktree, RefNameKind? Kind, string? Name);

/// <summary>The result of <c>ref.checkName</c>.</summary>
/// <param name="Problem">Why the name cannot be used, in plain words. Null when it can.</param>
internal sealed record RefNameCheck(string? Problem);

/// <summary>The params of <c>branch.create</c>.</summary>
internal sealed record BranchCreateParams
{
    public string? RepoId { get; init; }

    /// <summary>The worktree to switch, when <see cref="Switch"/> is set.</summary>
    public string? Worktree { get; init; }

    /// <summary>The full id of the commit the branch starts at.</summary>
    public string? Commit { get; init; }

    public string? Name { get; init; }

    /// <summary>Check the new branch out in the worktree too.</summary>
    public bool Switch { get; init; }
}

/// <summary>The params of <c>tag.create</c>.</summary>
internal sealed record TagCreateParams
{
    public string? RepoId { get; init; }

    public string? Worktree { get; init; }

    public string? Commit { get; init; }

    public string? Name { get; init; }

    /// <summary>The annotated tag's message. The name, when there is none.</summary>
    public string? Message { get; init; }
}

/// <summary>The params of <c>branch.switch</c>: exactly one of a branch, a remote branch or a commit.</summary>
internal sealed record BranchSwitchParams
{
    public string? RepoId { get; init; }

    public string? Worktree { get; init; }

    /// <summary>A local branch, such as "main".</summary>
    public string? Branch { get; init; }

    /// <summary>A remote-tracking branch, such as "origin/topic". A local branch that follows it is created.</summary>
    public string? RemoteBranch { get; init; }

    /// <summary>A full commit id, to check out on a detached HEAD.</summary>
    public string? Commit { get; init; }
}

/// <summary>The params of <c>branch.reset</c>: the worktree's current branch moves to the commit.</summary>
internal sealed record BranchResetParams(string? RepoId, string? Worktree, string? Commit, ResetMode? Mode);

/// <summary>The params of <c>branch.undoReset</c>: the result of <c>branch.reset</c>, as it was.</summary>
internal sealed record BranchUndoResetParams(string? RepoId, string? Worktree, ResetResult? Reset);

/// <summary>One hunk, as <c>changes.file</c> showed it.</summary>
internal sealed record ChangesHunkParams
{
    /// <summary>The file's <c>path</c>.</summary>
    public string? Path { get; init; }

    /// <summary>The file's <c>oldPath</c>, for a hunk of a staged rename.</summary>
    public string? OldPath { get; init; }

    /// <summary>The hunk's <c>id</c> from <c>changes.file</c>.</summary>
    public string? Id { get; init; }
}

/// <param name="Terminal">Null for the default: Windows Terminal when installed, otherwise Windows PowerShell.</param>
internal sealed record OpenTerminalParams(string? Path, TerminalKind? Terminal);

/// <summary>What the UI shows for one open repo. The result of <c>repo.open</c> and <c>repo.refresh</c>.</summary>
/// <param name="RepoRoot">The main worktree, whatever path the repo was opened with.</param>
/// <param name="TakenAt">When this snapshot was taken.</param>
/// <param name="UnreachableCountedAt">When unreachable commits were last counted. Their rows are as old as this. Null until the first scan.</param>
/// <param name="Scanning">
/// The scan after opening is still running: worktree state, leftovers and unreachable commits are not known yet, and
/// arrive in a <c>repo.changed</c> with the change kind <see cref="ChangeKind.Scanned"/>. Refs and worktrees are known.
/// </param>
/// <param name="ScanProblem">Why the scan after opening failed, in plain English. A refresh tries again.</param>
internal sealed record RepoState(
    string RepoId,
    string RepoRoot,
    string MainBranch,
    DateTimeOffset TakenAt,
    DateTimeOffset? UnreachableCountedAt,
    bool Scanning,
    string? ScanProblem,
    SnapshotSummary Snapshot,
    LeftoverSummary Summary,
    IReadOnlyList<Leftover> Leftovers,
    IReadOnlyList<Leftover> Hidden)
{
    /// <param name="rows">The rows to list, and those the user hid, which the counts leave out.</param>
    public static RepoState From(string repoId, RepoSnapshot s, SplitRows rows, ScanStatus scan) => From(repoId, s, rows.Shown, scan) with { Hidden = rows.Hidden };

    private static RepoState From(string repoId, RepoSnapshot s, IReadOnlyList<Leftover> leftovers, ScanStatus scan) => new(
        RepoId: repoId,
        RepoRoot: s.RepoRoot,
        MainBranch: s.MainBranch,
        TakenAt: s.TakenAt,
        UnreachableCountedAt: scan.UnreachableCountedAt,
        Scanning: scan.Scanning,
        ScanProblem: scan.Problem,
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
        Leftovers: leftovers,
        Hidden: []);
}

/// <summary>Where the scan after opening stands, for <see cref="RepoState"/>.</summary>
internal sealed record ScanStatus(DateTimeOffset? UnreachableCountedAt, bool Scanning, string? Problem);

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

    /// <summary>The scan after opening finished, or failed: the first full state, leftovers included.</summary>
    Scanned,
}

/// <summary>
/// The params of the <c>history.page</c> notification: the next commits of a <c>history.read</c>, in topological order.
/// Every page arrives before the read's own result.
/// </summary>
/// <param name="Index">0 for the first page, then 1, 2 and so on.</param>
internal sealed record HistoryPage(string Token, int Index, IReadOnlyList<CommitEntry> Commits);

/// <summary>The result of <c>history.read</c>, sent after its last page.</summary>
/// <param name="Tips">The commits the history was read from. Pass them to <c>history.update</c> to learn what changed since.</param>
internal sealed record HistoryReadResult(int Commits, int Pages, IReadOnlyList<string> Tips);

/// <summary>The result of <c>history.update</c>: how the history changes to match the repo's current refs and the toggles.</summary>
/// <param name="Added">New commits in topological order. They all go above the commits the UI has.</param>
/// <param name="Removed">Commits no ref shown reaches any more, such as those a force-move left behind.</param>
/// <param name="Tips">The commits the updated history starts from, for the next <c>history.update</c>.</param>
/// <param name="Reread">
/// Too much changed to send as an update: read the whole history again with <c>history.read</c>. Added and Removed
/// are then empty.
/// </param>
internal sealed record HistoryUpdateResult(IReadOnlyList<CommitEntry> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Tips, bool Reread);

/// <summary>Checkpoints and stashes are shown only when the UI asks for them.</summary>
internal enum HistoryRefKind { Branch, RemoteBranch, Tag, Checkpoint, Stash }

/// <summary>A ref history can show. Worktree HEADs are in the repo state.</summary>
/// <param name="Name">Short name: "feature/x", "origin/feature/x", "v1.0", "copilot/checkpoints/abc/1" or "stash@{0}".</param>
/// <param name="TargetId">The commit it points at, after peeling annotated tags.</param>
/// <param name="Agent">The agent it looks like it belongs to, from the patterns in diffract.json. A hint only.</param>
internal sealed record HistoryRef(string Name, HistoryRefKind Kind, string TargetId, string? Agent);

/// <summary>The result of <c>history.refs</c>: the refs, and what tells agent work apart.</summary>
/// <param name="AgentHeads">The HEAD of every worktree that looks like it belongs to an agent, by its folder or branch.</param>
/// <param name="AgentSubjectPrefixes">A commit whose subject starts with one of these looks like an agent made it.</param>
internal sealed record HistoryRefs(IReadOnlyList<HistoryRef> Refs, IReadOnlyList<string> AgentHeads, IReadOnlyList<string> AgentSubjectPrefixes);

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

/// <summary>The result of <c>changes.file</c>: the two sides of a file's diff, each as <c>history.file</c> answers, and its hunks.</summary>
internal sealed record ChangesFile
{
    /// <summary>HEAD for a staged diff, the index for an unstaged one. Null when the file is not there, as for a new file.</summary>
    public HistoryFile? Old { get; init; }

    /// <summary>The index for a staged diff, the file on disk for an unstaged one. Null when the file is not there, as for a deleted file.</summary>
    public HistoryFile? New { get; init; }

    /// <summary>What Stage hunk or Unstage hunk can act on. Empty when a side is missing, as the whole file is one hunk then.</summary>
    public required IReadOnlyList<Hunk> Hunks { get; init; }

    public static ChangesFile From(FileDiff diff) => new() { Old = Side(diff.Old), New = Side(diff.New), Hunks = diff.Hunks };

    private static HistoryFile? Side(FileContent? content) => content is null ? null : HistoryFile.From(content);
}
