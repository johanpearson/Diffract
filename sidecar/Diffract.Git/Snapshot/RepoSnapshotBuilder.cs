using System.Collections.Concurrent;
using System.Globalization;
using Diffract.Git.Parsing;

namespace Diffract.Git.Snapshot;

/// <summary>
/// Builds a <see cref="RepoSnapshot"/> using only read-only git commands and file timestamps.
/// Independent git calls run in parallel.
/// </summary>
public sealed class RepoSnapshotBuilder
{
    private const string Heads = "refs/heads/";
    private const string Remotes = "refs/remotes/";
    private const string Tags = "refs/tags/";
    private const string Stash = "refs/stash";
    private const string OriginHead = "refs/remotes/origin/HEAD";

    private readonly GitRunnerOptions _runnerOptions;
    private readonly SnapshotOptions _options;

    public RepoSnapshotBuilder(GitRunnerOptions runnerOptions, SnapshotOptions options)
    {
        if (!runnerOptions.ReadOnly)
            throw new ArgumentException("The snapshot builder only runs with a read-only git runner.", nameof(runnerOptions));
        _runnerOptions = runnerOptions;
        _options = options;
    }

    public async Task<SnapshotResult> BuildAsync(string repoPath, CancellationToken ct)
    {
        var call = new CallContext(_runnerOptions, _options.MaxParallelGitCalls);
        var takenAt = _runnerOptions.TimeProvider.GetUtcNow();
        repoPath = Path.GetFullPath(repoPath);
        if (!Directory.Exists(repoPath))
            throw new NotAGitRepositoryException(repoPath);

        var version = GitVersion.Parse((await call.RunAsync(repoPath, ["version"], ct).ConfigureAwait(false)).StandardOutput);
        if (!version.IsSupported)
            throw new GitVersionTooOldException(version);

        // Repo-wide reads that do not depend on each other.
        var worktreesTask = ListWorktreesAsync(call, repoPath, ct);
        var refsTask = call.RunAsync(repoPath, RefListParser.Arguments, ct);
        var stashesTask = ListStashesAsync(call, repoPath, worktreesTask, ct);
        var defaultBranchTask = call.RunAsync(repoPath, ["config", "--get", "init.defaultBranch"], [1], ct);
        var fsckTask = _options.IncludeUnreachable ? call.RunAsync(repoPath, UnreachableParser.Arguments, ct) : null;

        var worktreeEntries = await worktreesTask.ConfigureAwait(false);
        var refs = RefListParser.Parse((await refsTask.ConfigureAwait(false)).StandardOutput);
        var stashes = await stashesTask.ConfigureAwait(false);
        var defaultBranch = (await defaultBranchTask.ConfigureAwait(false)).StandardOutput.Trim();

        var refNames = refs.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var mainBranch = ResolveMainBranch(refs, refNames, defaultBranch);
        var mainRefs = new[] { Heads + mainBranch, Remotes + "origin/" + mainBranch }.Where(refNames.Contains).ToList();

        // Everything below depends on main.
        var mergedTask = ListMergedAsync(call, repoPath, mainRefs, ct);
        var worktreesInfoTask = DescribeWorktreesAsync(call, repoPath, worktreeEntries, mainRefs.FirstOrDefault(), ct);
        var riskTask = CountCommitsAtRiskAsync(call, repoPath, refs, mergedTask, ct);
        var unreachableTask = FindUnreachableCommitsAsync(call, repoPath, fsckTask, stashes, ct);

        var merged = await mergedTask.ConfigureAwait(false);
        var risk = await riskTask.ConfigureAwait(false);
        var worktrees = await worktreesInfoTask.ConfigureAwait(false);
        var unreachable = await unreachableTask.ConfigureAwait(false);

        var snapshot = new RepoSnapshot(
            RepoRoot: worktrees[0].Path,
            MainBranch: mainBranch,
            GitVersion: version,
            Worktrees: worktrees,
            Branches: BuildBranches(refs, merged, risk),
            Tags: BuildTags(refs, merged, risk),
            Stashes: stashes.Select(s => new StashInfo(s.Index, s.Name, s.CommitId, s.CreatedAt, s.Message)).ToList(),
            OtherRefs: refs.Where(r => IsOtherRef(r.Name)).Select(r => new RefInfo(r.Name, r.TargetId, r.CreatorDate)).ToList(),
            UnreachableCommits: unreachable,
            LeftoverFolders: FindLeftoverFolders(worktrees),
            TakenAt: takenAt);

        return new SnapshotResult(snapshot, call.Timings.ToList());
    }

    private static async Task<IReadOnlyList<WorktreeEntry>> ListWorktreesAsync(CallContext call, string repoPath, CancellationToken ct)
    {
        try
        {
            var result = await call.RunAsync(repoPath, WorktreeListParser.Arguments, ct).ConfigureAwait(false);
            return WorktreeListParser.Parse(result.StandardOutput);
        }
        catch (GitCommandException ex) when (ex.StandardError.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotAGitRepositoryException(repoPath);
        }
    }

    // Stashes are shared by all worktrees, but `stash list` refuses to run in a bare repo.
    private static async Task<IReadOnlyList<StashEntry>> ListStashesAsync(
        CallContext call, string repoPath, Task<IReadOnlyList<WorktreeEntry>> worktreesTask, CancellationToken ct)
    {
        var worktrees = await worktreesTask.ConfigureAwait(false);
        var bare = worktrees.Where(w => w.IsBare).Select(w => NormalizePath(w.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var workingDirectory = !bare.Contains(repoPath)
            ? repoPath
            : worktrees.Where(w => !w.IsBare && !w.IsPrunable).Select(w => NormalizePath(w.Path)).FirstOrDefault(Directory.Exists);
        if (workingDirectory is null)
            return [];

        var result = await call.RunAsync(workingDirectory, StashListParser.Arguments, ct).ConfigureAwait(false);
        return StashListParser.Parse(result.StandardOutput);
    }

    // auto: origin/HEAD, else init.defaultBranch if it exists, else main, else master.
    private string ResolveMainBranch(IReadOnlyList<RefEntry> refs, HashSet<string> refNames, string defaultBranch)
    {
        if (!string.Equals(_options.MainBranch, "auto", StringComparison.OrdinalIgnoreCase))
            return _options.MainBranch;

        var originHead = refs.FirstOrDefault(r => r.Name == OriginHead)?.SymrefTarget;
        if (originHead is not null && originHead.StartsWith(Remotes + "origin/", StringComparison.Ordinal))
            return originHead[(Remotes.Length + "origin/".Length)..];

        if (defaultBranch.Length > 0 && refNames.Contains(Heads + defaultBranch))
            return defaultBranch;

        return refNames.Contains(Heads + "main") || !refNames.Contains(Heads + "master") ? "main" : "master";
    }

    // Refs merged into local main or origin's main. Several --merged options are ORed together.
    private static async Task<HashSet<string>> ListMergedAsync(CallContext call, string repoPath, List<string> mainRefs, CancellationToken ct)
    {
        if (mainRefs.Count == 0)
            return [];

        List<string> args = ["for-each-ref", "--format=%(refname)"];
        args.AddRange(mainRefs.Select(r => "--merged=" + r));
        args.AddRange(["refs/heads", "refs/remotes", "refs/tags"]);
        var result = await call.RunAsync(repoPath, args, ct).ConfigureAwait(false);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, int>> CountCommitsAtRiskAsync(
        CallContext call, string repoPath, IReadOnlyList<RefEntry> refs, Task<HashSet<string>> mergedTask, CancellationToken ct)
    {
        var merged = await mergedTask.ConfigureAwait(false);
        var candidates = refs
            .Where(r => r.SymrefTarget is null && (r.Name.StartsWith(Heads, StringComparison.Ordinal)
                || r.Name.StartsWith(Remotes, StringComparison.Ordinal) || r.Name.StartsWith(Tags, StringComparison.Ordinal)))
            .Where(r => !merged.Contains(r.Name) && _options.AnalyzeRisk(r.Name))
            .ToList();

        var counts = await Task.WhenAll(candidates.Select(async r =>
        {
            var result = await call.RunAsync(repoPath, CommitsAtRiskArguments(r.Name), ct).ConfigureAwait(false);
            return (r.Name, Count: int.Parse(result.StandardOutput.Trim(), CultureInfo.InvariantCulture));
        })).ConfigureAwait(false);

        return counts.ToDictionary(c => c.Name, c => c.Count, StringComparer.Ordinal);
    }

    /// <summary>
    /// Commits reachable from <paramref name="fullName"/> and from no other branch, remote-tracking branch or tag.
    /// --exclude drops only the ref itself from the --branches, --remotes or --tags that follows it.
    /// Worktree HEADs and reflogs are deliberately not counted as protection.
    /// </summary>
    public static IReadOnlyList<string> CommitsAtRiskArguments(string fullName)
    {
        List<string> args = ["rev-list", "--count", fullName, "--not"];
        foreach (var (prefix, option) in new[] { (Heads, "--branches"), (Remotes, "--remotes"), (Tags, "--tags") })
        {
            if (fullName.StartsWith(prefix, StringComparison.Ordinal))
                args.Add("--exclude=" + fullName[prefix.Length..]);
            args.Add(option);
        }
        return args;
    }

    private async Task<IReadOnlyList<WorktreeInfo>> DescribeWorktreesAsync(
        CallContext call, string repoPath, IReadOnlyList<WorktreeEntry> entries, string? mainRef, CancellationToken ct)
    {
        var commitTimesTask = CommitTimesAsync(call, repoPath, entries.Select(e => e.Head).OfType<string>().Distinct().ToList(), ct);
        var infos = await Task.WhenAll(entries.Select(async (entry, index) =>
        {
            var path = NormalizePath(entry.Path);
            var canRunStatus = !entry.IsBare && !entry.IsPrunable && Directory.Exists(path);
            var statusTask = canRunStatus ? call.RunAsync(path, StatusParser.Arguments, ct) : null;
            var aheadBehindTask = mainRef is not null && entry.Head is not null && !entry.IsPrunable
                ? call.RunAsync(repoPath, ["rev-list", "--left-right", "--count", $"{mainRef}...{entry.Head}"], ct)
                : null;

            var status = statusTask is null ? null : StatusParser.Parse((await statusTask.ConfigureAwait(false)).StandardOutput);
            int? ahead = null, behind = null;
            if (aheadBehindTask is not null)
            {
                var counts = (await aheadBehindTask.ConfigureAwait(false)).StandardOutput.Split('\t', StringSplitOptions.TrimEntries);
                behind = int.Parse(counts[0], CultureInfo.InvariantCulture);
                ahead = int.Parse(counts[1], CultureInfo.InvariantCulture);
            }

            var commitTimes = await commitTimesTask.ConfigureAwait(false);
            DateTimeOffset? lastCommit = entry.Head is not null && commitTimes.TryGetValue(entry.Head, out var t) ? t : null;

            return new WorktreeInfo(
                Path: path,
                IsMain: index == 0,
                Head: entry.Head,
                Branch: entry.Branch is { } b && b.StartsWith(Heads, StringComparison.Ordinal) ? b[Heads.Length..] : entry.Branch,
                IsBare: entry.IsBare,
                IsDetached: entry.IsDetached,
                IsLocked: entry.IsLocked,
                LockReason: entry.LockReason,
                IsPrunable: entry.IsPrunable,
                PrunableReason: entry.PrunableReason,
                DirtyFileCount: status?.Entries.Count(e => e.Kind != StatusEntryKind.Ignored),
                AheadOfMain: ahead,
                BehindMain: behind,
                LastCommitAt: lastCommit,
                LastActivityAt: canRunStatus ? LastActivity(path, status!, lastCommit) : lastCommit);
        })).ConfigureAwait(false);

        return infos;
    }

    private static async Task<Dictionary<string, DateTimeOffset>> CommitTimesAsync(CallContext call, string repoPath, List<string> commits, CancellationToken ct)
    {
        if (commits.Count == 0)
            return [];

        List<string> args = ["log", "--no-walk=unsorted", "--format=%H %ct"];
        args.AddRange(commits);
        var result = await call.RunAsync(repoPath, args, ct).ConfigureAwait(false);
        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' '))
            .ToDictionary(p => p[0], p => DateTimeOffset.FromUnixTimeSeconds(long.Parse(p[1], CultureInfo.InvariantCulture)));
    }

    // Newest of: last commit, changed files listed by status, and the worktree's index file. Never walks the tree.
    private static DateTimeOffset? LastActivity(string worktreePath, StatusReport status, DateTimeOffset? lastCommit)
    {
        var newest = lastCommit;
        var paths = status.Entries
            .Where(e => e.Kind != StatusEntryKind.Ignored)
            .Select(e => Path.Combine(worktreePath, e.Path))
            .Append(GitDirectory.FindIndexFile(worktreePath));

        foreach (var path in paths.OfType<string>())
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists)
                continue;
            var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (newest is null || modified > newest)
                newest = modified;
        }
        return newest;
    }

    // fsck --no-reflogs reports stash@{1} and older as unreachable, because they only live in the
    // stash reflog. Commits that only stashes reach are not orphans, so they are left out.
    private static async Task<IReadOnlyList<string>> FindUnreachableCommitsAsync(
        CallContext call, string repoPath, Task<GitResult>? fsckTask, IReadOnlyList<StashEntry> stashes, CancellationToken ct)
    {
        if (fsckTask is null)
            return [];

        var unreachable = UnreachableParser.Parse((await fsckTask.ConfigureAwait(false)).StandardOutput)
            .Where(o => o.Type == "commit")
            .Select(o => o.Id)
            .ToList();
        if (unreachable.Count == 0 || stashes.Count == 0)
            return unreachable;

        List<string> args = ["rev-list"];
        args.AddRange(stashes.Select(s => s.CommitId));
        args.AddRange(["--not", "--branches", "--remotes", "--tags"]);
        var result = await call.RunAsync(repoPath, args, ct).ConfigureAwait(false);
        var stashOnly = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return unreachable.Where(id => !stashOnly.Contains(id)).ToList();
    }

    private static List<BranchInfo> BuildBranches(IReadOnlyList<RefEntry> refs, HashSet<string> merged, Dictionary<string, int> risk) =>
        refs.Where(r => r.SymrefTarget is null && (r.Name.StartsWith(Heads, StringComparison.Ordinal) || r.Name.StartsWith(Remotes, StringComparison.Ordinal)))
            .Select(r =>
            {
                var isRemote = r.Name.StartsWith(Remotes, StringComparison.Ordinal);
                var isMerged = merged.Contains(r.Name);
                return new BranchInfo(
                    Name: r.Name[(isRemote ? Remotes.Length : Heads.Length)..],
                    FullName: r.Name,
                    IsRemote: isRemote,
                    TargetId: r.TargetId,
                    LastCommitAt: r.CreatorDate,
                    Upstream: r.Upstream is { } u && u.StartsWith(Remotes, StringComparison.Ordinal) ? u[Remotes.Length..] : r.Upstream,
                    IsUpstreamGone: r.Tracking.IsGone,
                    IsMergedIntoMain: isMerged,
                    CommitsAtRisk: isMerged ? 0 : risk.TryGetValue(r.Name, out var n) ? n : null);
            })
            .ToList();

    private static List<TagInfo> BuildTags(IReadOnlyList<RefEntry> refs, HashSet<string> merged, Dictionary<string, int> risk) =>
        refs.Where(r => r.Name.StartsWith(Tags, StringComparison.Ordinal))
            .Select(r =>
            {
                var isMerged = merged.Contains(r.Name);
                return new TagInfo(
                    Name: r.Name[Tags.Length..],
                    FullName: r.Name,
                    TargetId: r.TargetId,
                    CreatedAt: r.CreatorDate,
                    IsMergedIntoMain: isMerged,
                    CommitsAtRisk: isMerged ? 0 : risk.TryGetValue(r.Name, out var n) ? n : null);
            })
            .ToList();

    private static bool IsOtherRef(string name) =>
        name != Stash
        && !name.StartsWith(Heads, StringComparison.Ordinal)
        && !name.StartsWith(Remotes, StringComparison.Ordinal)
        && !name.StartsWith(Tags, StringComparison.Ordinal);

    /// <summary>
    /// Subfolders of worktree container folders that are neither registered worktrees nor have a .git entry.
    /// Containers are the parent folders of linked worktrees, plus pattern paths with no wildcards
    /// below the repo root (such as .claude/worktrees). No other folders are ever listed.
    /// </summary>
    private List<string> FindLeftoverFolders(IReadOnlyList<WorktreeInfo> worktrees)
    {
        var patterns = _options.WorktreeContainerPatterns;
        if (patterns.Count == 0)
            return [];

        var registered = worktrees.Select(w => w.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var repoRoot = worktrees[0].Path;
        var containers = worktrees.Skip(1)
            .Select(w => Path.GetDirectoryName(w.Path))
            .OfType<string>()
            .Concat(patterns
                .Select(p => p.StartsWith("**/", StringComparison.Ordinal) ? p[3..] : null)
                .Where(p => p is not null && p.IndexOfAny(['*', '?']) < 0)
                .Select(p => NormalizePath(Path.Combine(repoRoot, p!))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(c => Directory.Exists(c) && GlobMatcher.IsMatchAny(patterns, c, ignoreCase: true));

        return containers
            .SelectMany(Directory.EnumerateDirectories)
            .Select(NormalizePath)
            .Where(d => !registered.Contains(d) && !GitDirectory.HasGitEntry(d))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Runs git calls with a parallelism cap and records how long each one took.</summary>
    private sealed class CallContext(GitRunnerOptions runnerOptions, int maxParallel)
    {
        private readonly SemaphoreSlim _gate = new(maxParallel);

        public ConcurrentQueue<GitCallTiming> Timings { get; } = new();

        public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken ct) =>
            RunAsync(workingDirectory, args, [], ct);

        public async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, IReadOnlyCollection<int> allowedExitCodes, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await new GitRunner(workingDirectory, runnerOptions).RunAsync(args, allowedExitCodes, ct).ConfigureAwait(false);
                Timings.Enqueue(new GitCallTiming("git " + string.Join(' ', args), result.Duration));
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
