using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Git.Snapshot;

namespace Diffract.Leftovers.Tests;

/// <summary>Hand-made snapshots for edge cases that are awkward to build as real repos.</summary>
internal static class Snap
{
    public static readonly DateTimeOffset Now = FixtureRepoBuilder.DefaultNow;

    public const string RepoRoot = @"C:\src\repo";

    public static RepoSnapshot Repo(IEnumerable<WorktreeInfo>? linked = null, IEnumerable<BranchInfo>? branches = null) => new(
        RepoRoot,
        "main",
        new GitVersion(2, 47, 1),
        [Worktree(RepoRoot, "main") with { IsMain = true }, .. linked ?? []],
        [Branch("main", merged: true), .. branches ?? []],
        [], [], [], [], [],
        Now);

    /// <summary>A clean worktree, last active three days ago: on its own, no rule cares about it.</summary>
    public static WorktreeInfo Worktree(string path, string? branch) => new(
        path, IsMain: false, Head: "abc123", branch, IsBare: false, IsDetached: branch is null, IsLocked: false, LockReason: null,
        IsPrunable: false, PrunableReason: null, DirtyFileCount: 0, AheadOfMain: 0, BehindMain: 0,
        LastCommitAt: Now.AddDays(-3), LastActivityAt: Now.AddDays(-3));

    public static BranchInfo Branch(string name, bool merged = false, int risk = 0, DateTimeOffset? lastCommit = null) => new(
        name, $"refs/heads/{name}", IsRemote: false, "abc123", lastCommit ?? Now.AddDays(-3), Upstream: null, IsUpstreamGone: false,
        IsMergedIntoMain: merged, CommitsAtRisk: merged ? 0 : risk);

    public static RefInfo Checkpoint(string session, int seq, DateTimeOffset at) =>
        new($"refs/copilot/checkpoints/{session}/{seq:D20}/id-{seq}", $"c{seq}", at);
}
