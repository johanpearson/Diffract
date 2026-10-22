using Diffract.Git.Parsing;

namespace Diffract.Git.Snapshot;

public sealed partial class RepoSnapshotBuilder
{
    /// <summary>
    /// Updates only what <paramref name="changes"/> names, starting from <paramref name="previous"/>.
    /// Never runs <c>fsck</c>: unreachable commits keep their previous value.
    /// <list type="bullet">
    /// <item>Files in a worktree: its status. 1 git call.</item>
    /// <item>A worktree's HEAD or index: its status and ahead/behind. 2 calls, 3 when HEAD moved to a commit no ref points at.</item>
    /// <item>Refs: refs, stashes, main branch, merged, commits at risk, and every worktree's ahead/behind.</item>
    /// <item>Worktree list: the list, leftover folders, and as for a HEAD change for worktrees that are new or moved.</item>
    /// <item>Everything: a full snapshot without <c>fsck</c>.</item>
    /// </list>
    /// </summary>
    public async Task<SnapshotResult> RefreshAsync(RepoSnapshot previous, ChangeSet changes, CancellationToken ct)
    {
        if (changes.Everything)
        {
            var full = await new RepoSnapshotBuilder(_runnerOptions, _options with { IncludeUnreachable = false })
                .BuildAsync(previous.RepoRoot, ct).ConfigureAwait(false);
            return full with { Snapshot = full.Snapshot with { UnreachableCommits = previous.UnreachableCommits } };
        }

        var call = new CallContext(_runnerOptions, _options.MaxParallelGitCalls, ct);
        var repo = new RepoCalls(call, previous.RepoRoot);
        var snapshot = previous with { TakenAt = _runnerOptions.TimeProvider.GetUtcNow() };
        var heads = new HashSet<string>(changes.WorktreeHeads, StringComparer.OrdinalIgnoreCase);

        if (changes.WorktreeList)
        {
            var entries = await ListWorktreesAsync(repo).ConfigureAwait(false);
            var (worktrees, changed) = MergeWorktreeList(previous.Worktrees, entries);
            snapshot = snapshot with { Worktrees = worktrees };
            heads.UnionWith(changed);
        }

        if (changes.Refs)
            snapshot = await RefreshRefsAsync(repo, snapshot).ConfigureAwait(false);

        // After a refs refresh every worktree already has its ahead/behind; only status is left to do.
        var status = new HashSet<string>([.. heads, .. changes.WorktreeFiles], StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> aheadBehind = changes.Refs ? new HashSet<string>() : heads;
        snapshot = await RefreshWorktreesAsync(repo, snapshot, previous, new WorktreeWork(status, aheadBehind)).ConfigureAwait(false);

        if (changes.WorktreeList)
            snapshot = snapshot with { LeftoverFolders = FindLeftoverFolders(snapshot.Worktrees) };

        return new SnapshotResult(snapshot, [.. call.Timings]);
    }

    /// <summary>The new worktree list, keeping what is known about worktrees that did not move. Returns the ones that are new or moved.</summary>
    private static (List<WorktreeInfo> Worktrees, List<string> Changed) MergeWorktreeList(IReadOnlyList<WorktreeInfo> previous, IReadOnlyList<WorktreeEntry> entries)
    {
        var known = previous.ToDictionary(w => w.Path, StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();
        var worktrees = entries.Select((entry, index) =>
        {
            var path = NormalizePath(entry.Path);
            var old = known.GetValueOrDefault(path);
            if (IsNewOrMoved(old, entry))
                changed.Add(path);

            return FromEntry(entry, path, index) with
            {
                DirtyFileCount = old?.DirtyFileCount,
                AheadOfMain = old?.AheadOfMain,
                BehindMain = old?.BehindMain,
                LastCommitAt = old?.LastCommitAt,
                LastActivityAt = old?.LastActivityAt,
            };
        }).ToList();
        return (worktrees, changed);
    }

    private static bool IsNewOrMoved(WorktreeInfo? old, WorktreeEntry entry) =>
        old is null || old.Head != entry.Head || old.IsPrunable != entry.IsPrunable;

    /// <summary>Everything that depends on refs, as in a full snapshot, without listing worktrees or running status.</summary>
    private async Task<RepoSnapshot> RefreshRefsAsync(RepoCalls repo, RepoSnapshot snapshot)
    {
        var refsTask = repo.RunAsync(RefListParser.Arguments);
        var stashesTask = ListStashesAsync(repo, snapshot.Worktrees.Select(w => (w.Path, w.IsBare, w.IsPrunable)));
        var defaultBranchTask = repo.Call.RunAsync(repo.Path, ["config", "--get", "init.defaultBranch"], [1]);

        var refs = RefListParser.Parse((await refsTask.ConfigureAwait(false)).StandardOutput);
        var stashes = await stashesTask.ConfigureAwait(false);
        var defaultBranch = (await defaultBranchTask.ConfigureAwait(false)).StandardOutput.Trim();

        var refNames = refs.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var mainBranch = ResolveMainBranch(refs, refNames, defaultBranch);
        var mainRefs = new[] { Heads + mainBranch, Remotes + "origin/" + mainBranch }.Where(refNames.Contains).ToList();

        var mergedTask = ListMergedAsync(repo, mainRefs);
        var riskTask = CountCommitsAtRiskAsync(repo, refs, mergedTask);
        var merged = await mergedTask.ConfigureAwait(false);
        var risk = await riskTask.ConfigureAwait(false);

        // A worktree on a branch is wherever that branch is now. Detached ones stay where they were.
        var tips = refs.Where(r => r.Name.StartsWith(Heads, StringComparison.Ordinal)).ToDictionary(r => r.Name[Heads.Length..], StringComparer.Ordinal);
        var worktrees = snapshot.Worktrees.Select(w => w.Branch is { } b && tips.TryGetValue(b, out var tip)
            ? w with { Head = tip.TargetId, LastCommitAt = tip.CreatorDate, LastActivityAt = Newest(w.LastActivityAt, tip.CreatorDate) }
            : w).ToList();

        var withRefs = snapshot with
        {
            MainBranch = mainBranch,
            Worktrees = worktrees,
            Branches = BuildBranches(refs, merged, risk),
            Tags = BuildTags(refs, merged, risk),
            Stashes = [.. stashes.Select(s => new StashInfo(s.Index, s.Name, s.CommitId, s.CreatedAt, s.Message))],
            OtherRefs = [.. refs.Where(r => IsOtherRef(r.Name)).Select(r => new RefInfo(r.Name, r.TargetId, r.CreatorDate))],
        };
        var all = worktrees.Select(w => w.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return await RefreshWorktreesAsync(repo, withRefs, snapshot, new WorktreeWork(new HashSet<string>(), all)).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs status for the worktrees in <see cref="WorktreeWork.Status"/> and ahead/behind for those in
    /// <see cref="WorktreeWork.AheadBehind"/>, in parallel. Ahead/behind runs in the worktree itself against its HEAD,
    /// so it does not wait for status to say where HEAD is.
    /// </summary>
    private static async Task<RepoSnapshot> RefreshWorktreesAsync(RepoCalls repo, RepoSnapshot snapshot, RepoSnapshot previous, WorktreeWork work)
    {
        if (work.IsEmpty)
            return snapshot;

        var mainRef = new[] { Heads + snapshot.MainBranch, Remotes + "origin/" + snapshot.MainBranch }
            .FirstOrDefault(r => snapshot.Branches.Any(b => b.FullName == r));
        var before = previous.Worktrees.ToDictionary(w => w.Path, StringComparer.OrdinalIgnoreCase);
        var refresh = new RefreshContext(repo.Call, work, mainRef, before);

        var updated = await Task.WhenAll(snapshot.Worktrees.Select(w => RefreshWorktreeAsync(refresh, w))).ConfigureAwait(false);
        var times = await CommitTimesOfMovedAsync(repo, snapshot, updated).ConfigureAwait(false);
        return snapshot with { Worktrees = [.. updated.Select(u => Settle(u, times))] };
    }

    private static async Task<RefreshedWorktree> RefreshWorktreeAsync(RefreshContext refresh, WorktreeInfo w)
    {
        var canRunStatus = !w.IsBare && !w.IsPrunable && Directory.Exists(w.Path);
        var statusTask = canRunStatus && refresh.Work.Status.Contains(w.Path)
            ? ReadStatusAsync(refresh.Call, w.Path)
            : Task.FromResult<StatusReport?>(null);
        var aheadBehindTask = WantsAheadBehind(refresh, w, canRunStatus)
            ? refresh.Call.RunAsync(w.Path, ["rev-list", "--left-right", "--count", $"{refresh.MainRef}...HEAD"], [128])
            : null;

        var report = await statusTask.ConfigureAwait(false);
        var result = WithStatus(w, report);
        if (aheadBehindTask is not null)
            result = WithAheadBehind(result, await aheadBehindTask.ConfigureAwait(false));
        return new RefreshedWorktree(result, report, refresh.Before.GetValueOrDefault(w.Path)?.Head != result.Head);
    }

    /// <summary>Ahead/behind runs in the worktree, so it needs a folder: a bare repo's own, or one status can run in.</summary>
    private static bool WantsAheadBehind(RefreshContext refresh, WorktreeInfo w, bool canRunStatus) =>
        refresh.MainRef is not null && (w.IsBare || canRunStatus) && refresh.Work.AheadBehind.Contains(w.Path);

    private static WorktreeInfo WithStatus(WorktreeInfo w, StatusReport? report) => report is null ? w : w with
    {
        Head = report.Branch.Oid,
        Branch = report.Branch.Head,
        IsDetached = report.Branch.IsDetached,
        DirtyFileCount = DirtyFiles(report),
    };

    // 128: HEAD does not point at a commit yet, as in a worktree on an unborn branch.
    private static WorktreeInfo WithAheadBehind(WorktreeInfo w, GitResult counts)
    {
        if (counts.ExitCode != 0)
            return w;

        var (behind, ahead) = ParseLeftRight(counts);
        return w with { BehindMain = behind, AheadOfMain = ahead };
    }

    /// <summary>
    /// Commit times for worktrees whose HEAD moved. A branch's date is its tip's commit date, so a HEAD that moved onto
    /// a branch tip needs no extra call. HEADs that moved to a commit no branch points at take one call for all of them.
    /// </summary>
    private static async Task<CommitTimes> CommitTimesOfMovedAsync(RepoCalls repo, RepoSnapshot snapshot, RefreshedWorktree[] updated)
    {
        var tipTimes = snapshot.Branches.Select(b => (b.TargetId, b.LastCommitAt))
            .Where(t => t.LastCommitAt is not null)
            .DistinctBy(t => t.TargetId)
            .ToDictionary(t => t.TargetId, t => t.LastCommitAt!.Value, StringComparer.Ordinal);
        var unknown = updated
            .Where(u => u.Moved && u.Worktree.Head is { } h && !tipTimes.ContainsKey(h))
            .Select(u => u.Worktree.Head!)
            .Distinct()
            .ToList();
        return new CommitTimes(tipTimes, await CommitTimesAsync(repo, unknown).ConfigureAwait(false));
    }

    private static WorktreeInfo Settle(RefreshedWorktree u, CommitTimes times)
    {
        var w = u.Moved ? u.Worktree with { LastCommitAt = times.Of(u.Worktree) } : u.Worktree;
        return u.Status is null ? w : w with { LastActivityAt = LastActivity(w.Path, u.Status, w.LastCommitAt) };
    }

    private static string? ShortBranch(string? branch) =>
        branch is { } b && b.StartsWith(Heads, StringComparison.Ordinal) ? b[Heads.Length..] : branch;

    private static DateTimeOffset? Newest(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>Which worktrees need status, and which need ahead/behind.</summary>
    private sealed record WorktreeWork(IReadOnlySet<string> Status, IReadOnlySet<string> AheadBehind)
    {
        public bool IsEmpty => Status.Count == 0 && AheadBehind.Count == 0;
    }

    private sealed record RefreshContext(CallContext Call, WorktreeWork Work, string? MainRef, Dictionary<string, WorktreeInfo> Before);

    private sealed record RefreshedWorktree(WorktreeInfo Worktree, StatusReport? Status, bool Moved);

    /// <summary>Commit times from branch tips first, then from the extra call.</summary>
    private sealed record CommitTimes(Dictionary<string, DateTimeOffset> Tips, Dictionary<string, DateTimeOffset> Others)
    {
        public DateTimeOffset? Of(WorktreeInfo w) =>
            w.Head is null ? null
            : Tips.TryGetValue(w.Head, out var t) ? t
            : Others.TryGetValue(w.Head, out var c) ? c
            : w.LastCommitAt;
    }
}
