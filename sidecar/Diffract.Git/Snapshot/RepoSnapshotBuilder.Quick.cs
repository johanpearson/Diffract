using Diffract.Git.Parsing;

namespace Diffract.Git.Snapshot;

public sealed partial class RepoSnapshotBuilder
{
    /// <summary>
    /// What history needs, and nothing slower: the worktrees as <c>worktree list</c> gives them, the refs and the main
    /// branch. 4 git calls, 3 of them in parallel. Worktree state, merged branches, commits at risk, stashes, leftover
    /// folders and unreachable commits are left empty; <see cref="BuildAsync"/> fills them in.
    /// </summary>
    public async Task<SnapshotResult> BuildQuickAsync(string repoPath, CancellationToken ct)
    {
        var call = new CallContext(_runnerOptions, _options.MaxParallelGitCalls, ct);
        var takenAt = _runnerOptions.TimeProvider.GetUtcNow();
        var (repo, version) = await StartAsync(call, repoPath).ConfigureAwait(false);
        repoPath = repo.Path;

        var worktreesTask = ListWorktreesAsync(repo);
        var refsTask = repo.RunAsync(RefListParser.Arguments);
        var defaultBranchTask = call.RunAsync(repoPath, ["config", "--get", "init.defaultBranch"], [1]);

        var entries = await worktreesTask.ConfigureAwait(false);
        var refs = RefListParser.Parse((await refsTask.ConfigureAwait(false)).StandardOutput);
        var defaultBranch = (await defaultBranchTask.ConfigureAwait(false)).StandardOutput.Trim();
        var mainBranch = ResolveMainBranch(refs, refs.Select(r => r.Name).ToHashSet(StringComparer.Ordinal), defaultBranch);
        var worktrees = entries.Select((entry, index) => FromEntry(entry, NormalizePath(entry.Path), index)).ToList();

        var snapshot = new RepoSnapshot(
            RepoRoot: worktrees[0].Path,
            MainBranch: mainBranch,
            GitVersion: version,
            Worktrees: worktrees,
            Branches: BuildBranches(refs, [], []),
            Tags: BuildTags(refs, [], []),
            Stashes: [],
            OtherRefs: refs.Where(r => IsOtherRef(r.Name)).Select(r => new RefInfo(r.Name, r.TargetId, r.CreatorDate)).ToList(),
            UnreachableCommits: [],
            LeftoverFolders: [],
            TakenAt: takenAt);

        return new SnapshotResult(snapshot, call.Timings.ToList());
    }
}
