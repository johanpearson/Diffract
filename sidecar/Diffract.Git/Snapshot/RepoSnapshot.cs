namespace Diffract.Git.Snapshot;

/// <summary>One immutable picture of a repo and all its worktrees.</summary>
/// <param name="UnreachableCommits">Commits that no ref and no reflog points to. Empty when fsck was skipped.</param>
public sealed record RepoSnapshot(
    string RepoRoot,
    string MainBranch,
    GitVersion GitVersion,
    IReadOnlyList<WorktreeInfo> Worktrees,
    IReadOnlyList<BranchInfo> Branches,
    IReadOnlyList<TagInfo> Tags,
    IReadOnlyList<StashInfo> Stashes,
    IReadOnlyList<RefInfo> OtherRefs,
    IReadOnlyList<UnreachableCommit> UnreachableCommits,
    IReadOnlyList<string> LeftoverFolders,
    DateTimeOffset TakenAt);

/// <param name="Path">Full Windows path.</param>
/// <param name="Branch">Short branch name, or null when detached or bare.</param>
/// <param name="DirtyFileCount">Changed and untracked files. Null when status could not run (bare or prunable).</param>
/// <param name="AheadOfMain">Commits on this worktree's HEAD that main does not have. Null when there is no main to compare with.</param>
/// <param name="LastActivityAt">Newest of the last commit, changed files and the worktree's index file.</param>
public sealed record WorktreeInfo(
    string Path,
    bool IsMain,
    string? Head,
    string? Branch,
    bool IsBare,
    bool IsDetached,
    bool IsLocked,
    string? LockReason,
    bool IsPrunable,
    string? PrunableReason,
    int? DirtyFileCount,
    int? AheadOfMain,
    int? BehindMain,
    DateTimeOffset? LastCommitAt,
    DateTimeOffset? LastActivityAt);

/// <param name="Name">Short name: "feature/x" for local, "origin/feature/x" for remote-tracking.</param>
/// <param name="Upstream">Short upstream name for local branches, for example "origin/feature/x".</param>
/// <param name="IsUpstreamGone">The upstream was deleted on the remote, usually after a squash merge.</param>
/// <param name="CommitsAtRisk">
/// Commits reachable from this branch and from no other branch, remote-tracking branch or tag.
/// Null when not calculated (see <see cref="SnapshotOptions.AnalyzeRisk"/>).
/// </param>
public sealed record BranchInfo(
    string Name,
    string FullName,
    bool IsRemote,
    string TargetId,
    DateTimeOffset? LastCommitAt,
    string? Upstream,
    bool IsUpstreamGone,
    bool IsMergedIntoMain,
    int? CommitsAtRisk);

/// <param name="TargetId">The commit the tag points at, after peeling annotated tags.</param>
/// <param name="CreatedAt">Tagger date for annotated tags, commit date for lightweight tags.</param>
public sealed record TagInfo(
    string Name,
    string FullName,
    string TargetId,
    DateTimeOffset? CreatedAt,
    bool IsMergedIntoMain,
    int? CommitsAtRisk);

/// <summary>A commit that no ref and no reflog points to.</summary>
/// <param name="Subject">The first line of the commit message.</param>
public sealed record UnreachableCommit(string Id, DateTimeOffset CommittedAt, string Subject);

public sealed record StashInfo(int Index, string Name, string CommitId, DateTimeOffset CreatedAt, string Message);

/// <summary>A ref outside refs/heads, refs/tags, refs/remotes and refs/stash, such as a Copilot checkpoint.</summary>
public sealed record RefInfo(string FullName, string TargetId, DateTimeOffset? CreatedAt);

public sealed record GitCallTiming(string Command, TimeSpan Duration);

public sealed record SnapshotResult(RepoSnapshot Snapshot, IReadOnlyList<GitCallTiming> Timings);
