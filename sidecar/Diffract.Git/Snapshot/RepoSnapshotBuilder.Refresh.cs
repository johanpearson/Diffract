using System.Globalization;
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

        var call = new CallContext(_runnerOptions, _options.MaxParallelGitCalls);
        var snapshot = previous with { TakenAt = _runnerOptions.TimeProvider.GetUtcNow() };
        var heads = new HashSet<string>(changes.WorktreeHeads, StringComparer.OrdinalIgnoreCase);

        if (changes.WorktreeList)
        {
            var entries = await ListWorktreesAsync(call, previous.RepoRoot, ct).ConfigureAwait(false);
            var (worktrees, changed) = MergeWorktreeList(previous.Worktrees, entries);
            snapshot = snapshot with { Worktrees = worktrees };
            heads.UnionWith(changed);
        }

        if (changes.Refs)
            snapshot = await RefreshRefsAsync(call, snapshot, ct).ConfigureAwait(false);

        // After a refs refresh every worktree already has its ahead/behind; only status is left to do.
        var status = new HashSet<string>([.. heads, .. changes.WorktreeFiles], StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> aheadBehind = changes.Refs ? new HashSet<string>() : heads;
        snapshot = await RefreshWorktreesAsync(call, snapshot, previous, status, aheadBehind, ct).ConfigureAwait(false);

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
            if (old is null || old.Head != entry.Head || old.IsPrunable != entry.IsPrunable)
                changed.Add(path);

            return new WorktreeInfo(
                Path: path,
                IsMain: index == 0,
                Head: entry.Head,
                Branch: ShortBranch(entry.Branch),
                IsBare: entry.IsBare,
                IsDetached: entry.IsDetached,
                IsLocked: entry.IsLocked,
                LockReason: entry.LockReason,
                IsPrunable: entry.IsPrunable,
                PrunableReason: entry.PrunableReason,
                DirtyFileCount: old?.DirtyFileCount,
                AheadOfMain: old?.AheadOfMain,
                BehindMain: old?.BehindMain,
                LastCommitAt: old?.LastCommitAt,
                LastActivityAt: old?.LastActivityAt);
        }).ToList();
        return (worktrees, changed);
    }

    /// <summary>Everything that depends on refs, as in a full snapshot, without listing worktrees or running status.</summary>
    private async Task<RepoSnapshot> RefreshRefsAsync(CallContext call, RepoSnapshot snapshot, CancellationToken ct)
    {
        var repoPath = snapshot.RepoRoot;
        var refsTask = call.RunAsync(repoPath, RefListParser.Arguments, ct);
        var stashesTask = ListStashesAsync(call, repoPath, snapshot.Worktrees.Select(w => (w.Path, w.IsBare, w.IsPrunable)), ct);
        var defaultBranchTask = call.RunAsync(repoPath, ["config", "--get", "init.defaultBranch"], [1], ct);

        var refs = RefListParser.Parse((await refsTask.ConfigureAwait(false)).StandardOutput);
        var stashes = await stashesTask.ConfigureAwait(false);
        var defaultBranch = (await defaultBranchTask.ConfigureAwait(false)).StandardOutput.Trim();

        var refNames = refs.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var mainBranch = ResolveMainBranch(refs, refNames, defaultBranch);
        var mainRefs = new[] { Heads + mainBranch, Remotes + "origin/" + mainBranch }.Where(refNames.Contains).ToList();

        var mergedTask = ListMergedAsync(call, repoPath, mainRefs, ct);
        var riskTask = CountCommitsAtRiskAsync(call, repoPath, refs, mergedTask, ct);
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
        return await RefreshWorktreesAsync(call, withRefs, snapshot, new HashSet<string>(), all, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs status for the worktrees in <paramref name="status"/> and ahead/behind for those in <paramref name="aheadBehind"/>,
    /// in parallel. Ahead/behind runs in the worktree itself against its HEAD, so it does not wait for status to say where HEAD is.
    /// </summary>
    private async Task<RepoSnapshot> RefreshWorktreesAsync(
        CallContext call, RepoSnapshot snapshot, RepoSnapshot previous, IReadOnlySet<string> status, IReadOnlySet<string> aheadBehind, CancellationToken ct)
    {
        if (status.Count == 0 && aheadBehind.Count == 0)
            return snapshot;

        var mainRef = new[] { Heads + snapshot.MainBranch, Remotes + "origin/" + snapshot.MainBranch }
            .FirstOrDefault(r => snapshot.Branches.Any(b => b.FullName == r));
        // A branch's date is its tip's commit date, so a HEAD that moved onto a branch tip needs no extra call.
        var tipTimes = snapshot.Branches.Select(b => (b.TargetId, b.LastCommitAt))
            .Where(t => t.LastCommitAt is not null)
            .DistinctBy(t => t.TargetId)
            .ToDictionary(t => t.TargetId, t => t.LastCommitAt!.Value, StringComparer.Ordinal);
        var before = previous.Worktrees.ToDictionary(w => w.Path, StringComparer.OrdinalIgnoreCase);

        var updated = await Task.WhenAll(snapshot.Worktrees.Select(async w =>
        {
            var canRunStatus = !w.IsBare && !w.IsPrunable && Directory.Exists(w.Path);
            var statusTask = canRunStatus && status.Contains(w.Path) ? call.RunAsync(w.Path, StatusParser.Arguments, ct) : null;
            var workingDirectory = w.IsBare ? w.Path : canRunStatus ? w.Path : null;
            var aheadBehindTask = mainRef is not null && workingDirectory is not null && aheadBehind.Contains(w.Path)
                ? call.RunAsync(workingDirectory, ["rev-list", "--left-right", "--count", $"{mainRef}...HEAD"], [128], ct)
                : null;

            var report = statusTask is null ? null : StatusParser.Parse((await statusTask.ConfigureAwait(false)).StandardOutput);
            var result = w;
            if (report is not null)
            {
                result = result with
                {
                    Head = report.Branch.Oid,
                    Branch = report.Branch.Head,
                    IsDetached = report.Branch.IsDetached,
                    DirtyFileCount = report.Entries.Count(e => e.Kind != StatusEntryKind.Ignored),
                };
            }

            if (aheadBehindTask is not null)
            {
                var counts = await aheadBehindTask.ConfigureAwait(false);
                // 128: HEAD does not point at a commit yet, as in a worktree on an unborn branch.
                if (counts.ExitCode == 0)
                {
                    var parts = counts.StandardOutput.Split('\t', StringSplitOptions.TrimEntries);
                    result = result with
                    {
                        BehindMain = int.Parse(parts[0], CultureInfo.InvariantCulture),
                        AheadOfMain = int.Parse(parts[1], CultureInfo.InvariantCulture),
                    };
                }
            }
            return (Worktree: result, Status: report, Moved: before.GetValueOrDefault(w.Path)?.Head != result.Head);
        })).ConfigureAwait(false);

        // HEAD moved to a commit no branch points at: one call for all of them.
        var unknown = updated.Where(u => u.Moved && u.Worktree.Head is { } h && !tipTimes.ContainsKey(h)).Select(u => u.Worktree.Head!).Distinct().ToList();
        var commitTimes = await CommitTimesAsync(call, snapshot.RepoRoot, unknown, ct).ConfigureAwait(false);

        var worktrees = updated.Select(u =>
        {
            var w = u.Worktree;
            if (u.Moved)
            {
                w = w with
                {
                    LastCommitAt = w.Head is null ? null
                        : tipTimes.TryGetValue(w.Head, out var t) ? t
                        : commitTimes.TryGetValue(w.Head, out var c) ? c
                        : w.LastCommitAt,
                };
            }
            return u.Status is null ? w : w with { LastActivityAt = LastActivity(w.Path, u.Status, w.LastCommitAt) };
        }).ToList();

        return snapshot with { Worktrees = worktrees };
    }

    private static string? ShortBranch(string? branch) =>
        branch is { } b && b.StartsWith(Heads, StringComparison.Ordinal) ? b[Heads.Length..] : branch;

    private static DateTimeOffset? Newest(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
