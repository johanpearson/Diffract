using System.Collections.Concurrent;
using System.Globalization;
using Diffract.Git.Parsing;

namespace Diffract.Git.Snapshot;

/// <summary>
/// Builds a <see cref="RepoSnapshot"/> using only read-only git commands and file timestamps.
/// Independent git calls run in parallel.
/// </summary>
public sealed partial class RepoSnapshotBuilder
{
    private const string Heads = "refs/heads/";
    private const string Remotes = "refs/remotes/";
    private const string Tags = "refs/tags/";
    private const string Stash = "refs/stash";
    private const string OriginHead = "refs/remotes/origin/HEAD";
    private const int UnreachableBatchSize = 200;

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
        var call = new CallContext(_runnerOptions, _options.MaxParallelGitCalls, ct);
        var takenAt = _runnerOptions.TimeProvider.GetUtcNow();
        var (repo, version) = await StartAsync(call, repoPath).ConfigureAwait(false);
        repoPath = repo.Path;

        // Repo-wide reads that do not depend on each other.
        var worktreesTask = ListWorktreesAsync(repo);
        var refsTask = repo.RunAsync(RefListParser.Arguments);
        var stashesTask = ListStashesAsync(repo, worktreesTask);
        var defaultBranchTask = call.RunAsync(repoPath, ["config", "--get", "init.defaultBranch"], [1]);
        var fsckTask = _options.IncludeUnreachable ? repo.RunAsync(UnreachableParser.Arguments) : null;

        var worktreeEntries = await worktreesTask.ConfigureAwait(false);
        var refs = RefListParser.Parse((await refsTask.ConfigureAwait(false)).StandardOutput);
        var stashes = await stashesTask.ConfigureAwait(false);
        var defaultBranch = (await defaultBranchTask.ConfigureAwait(false)).StandardOutput.Trim();

        var refNames = refs.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var mainBranch = ResolveMainBranch(refs, refNames, defaultBranch);
        var mainRefs = new[] { Heads + mainBranch, Remotes + "origin/" + mainBranch }.Where(refNames.Contains).ToList();

        // Everything below depends on main.
        var mergedTask = ListMergedAsync(repo, mainRefs);
        var worktreesInfoTask = DescribeWorktreesAsync(repo, worktreeEntries, mainRefs.FirstOrDefault());
        var riskTask = CountCommitsAtRiskAsync(repo, refs, mergedTask);
        var unreachableTask = FindUnreachableCommitsAsync(repo, fsckTask);

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

    /// <summary>Checks the folder exists and git is new enough. Every build starts here.</summary>
    private static async Task<(RepoCalls Repo, GitVersion Version)> StartAsync(CallContext call, string repoPath)
    {
        repoPath = Path.GetFullPath(repoPath);
        if (!Directory.Exists(repoPath))
            throw new NotAGitRepositoryException(repoPath);

        var repo = new RepoCalls(call, repoPath);
        var version = GitVersion.Parse((await repo.RunAsync(["version"]).ConfigureAwait(false)).StandardOutput);
        if (!version.IsSupported)
            throw new GitVersionTooOldException(version);
        return (repo, version);
    }

    private static async Task<IReadOnlyList<WorktreeEntry>> ListWorktreesAsync(RepoCalls repo)
    {
        try
        {
            var result = await repo.RunAsync(WorktreeListParser.Arguments).ConfigureAwait(false);
            return WorktreeListParser.Parse(result.StandardOutput);
        }
        // Newer git words a broken .git file differently ("gitfile does not point to a valid repository"),
        // so that case is recognized by the link itself, not by the message.
        catch (GitCommandException ex) when (ex.StandardError.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
            || ExplainBrokenLink(repo.Path) is not null)
        {
            throw new NotAGitRepositoryException(repo.Path, ExplainBrokenLink(repo.Path));
        }
    }

    // A folder whose .git file points to worktree records that are gone: the worktree was removed or
    // pruned in git, but the folder stayed.
    private static string? ExplainBrokenLink(string folder)
    {
        if (GitDirectory.ReadGitFileLink(folder) is not { } link || Directory.Exists(link))
            return null;

        return GitDirectory.OwnerOfWorktreeRecord(link) is { } owner
            ? $"It was a worktree of {owner}, but that repo no longer knows it."
            : $"Its .git file points to {link}, which no longer exists.";
    }

    // Stashes are shared by all worktrees, but `stash list` refuses to run in a bare repo.
    private static async Task<IReadOnlyList<StashEntry>> ListStashesAsync(RepoCalls repo, Task<IReadOnlyList<WorktreeEntry>> worktreesTask)
    {
        var worktrees = await worktreesTask.ConfigureAwait(false);
        return await ListStashesAsync(repo, worktrees.Select(w => (w.Path, w.IsBare, w.IsPrunable))).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<StashEntry>> ListStashesAsync(RepoCalls repo, IEnumerable<(string Path, bool IsBare, bool IsPrunable)> worktrees)
    {
        var bare = worktrees.Where(w => w.IsBare).Select(w => NormalizePath(w.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var workingDirectory = !bare.Contains(repo.Path)
            ? repo.Path
            : worktrees.Where(w => !w.IsBare && !w.IsPrunable).Select(w => NormalizePath(w.Path)).FirstOrDefault(Directory.Exists);
        if (workingDirectory is null)
            return [];

        var result = await repo.Call.RunAsync(workingDirectory, StashListParser.Arguments).ConfigureAwait(false);
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
    private static async Task<HashSet<string>> ListMergedAsync(RepoCalls repo, List<string> mainRefs)
    {
        if (mainRefs.Count == 0)
            return [];

        List<string> args = ["for-each-ref", "--format=%(refname)"];
        args.AddRange(mainRefs.Select(r => "--merged=" + r));
        args.AddRange(["refs/heads", "refs/remotes", "refs/tags"]);
        var result = await repo.RunAsync(args).ConfigureAwait(false);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, int>> CountCommitsAtRiskAsync(RepoCalls repo, IReadOnlyList<RefEntry> refs, Task<HashSet<string>> mergedTask)
    {
        var merged = await mergedTask.ConfigureAwait(false);
        var candidates = refs
            .Where(r => r.SymrefTarget is null && IsBranchOrTag(r.Name))
            .Where(r => !merged.Contains(r.Name) && _options.AnalyzeRisk(r.Name))
            .ToList();

        var counts = await Task.WhenAll(candidates.Select(async r =>
        {
            var result = await repo.RunAsync(CommitsAtRiskArguments(r.Name)).ConfigureAwait(false);
            return (r.Name, Count: int.Parse(result.StandardOutput.Trim(), CultureInfo.InvariantCulture));
        })).ConfigureAwait(false);

        return counts.ToDictionary(c => c.Name, c => c.Count, StringComparer.Ordinal);
    }

    private static bool IsBranchOrTag(string name) =>
        name.StartsWith(Heads, StringComparison.Ordinal)
        || name.StartsWith(Remotes, StringComparison.Ordinal)
        || name.StartsWith(Tags, StringComparison.Ordinal);

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

    private static async Task<IReadOnlyList<WorktreeInfo>> DescribeWorktreesAsync(RepoCalls repo, IReadOnlyList<WorktreeEntry> entries, string? mainRef)
    {
        var commitTimes = CommitTimesAsync(repo, entries.Select(e => e.Head).OfType<string>().Distinct().ToList());
        var describe = new DescribeContext(repo, mainRef, commitTimes);
        return await Task.WhenAll(entries.Select((entry, index) => DescribeWorktreeAsync(describe, entry, index))).ConfigureAwait(false);
    }

    private static async Task<WorktreeInfo> DescribeWorktreeAsync(DescribeContext describe, WorktreeEntry entry, int index)
    {
        var path = NormalizePath(entry.Path);
        var canRunStatus = !entry.IsBare && !entry.IsPrunable && Directory.Exists(path);
        var statusTask = canRunStatus ? ReadStatusAsync(describe.Repo.Call, path) : Task.FromResult<StatusReport?>(null);
        var aheadBehindTask = AheadBehindOfEntryAsync(describe, entry);

        var status = await statusTask.ConfigureAwait(false);
        var (ahead, behind) = await aheadBehindTask.ConfigureAwait(false);
        var lastCommit = CommitTimeOf(entry, await describe.CommitTimes.ConfigureAwait(false));

        return FromEntry(entry, path, index) with
        {
            DirtyFileCount = DirtyFiles(status),
            AheadOfMain = ahead,
            BehindMain = behind,
            LastCommitAt = lastCommit,
            LastActivityAt = canRunStatus ? LastActivity(path, status!, lastCommit) : lastCommit,
        };
    }

    /// <summary>A worktree as <c>worktree list</c> describes it, with nothing yet known about its state.</summary>
    private static WorktreeInfo FromEntry(WorktreeEntry entry, string path, int index) => new(
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
        DirtyFileCount: null,
        AheadOfMain: null,
        BehindMain: null,
        LastCommitAt: null,
        LastActivityAt: null);

    private static async Task<StatusReport?> ReadStatusAsync(CallContext call, string worktreePath) =>
        StatusParser.Parse((await call.RunAsync(worktreePath, StatusParser.Arguments).ConfigureAwait(false)).StandardOutput);

    private static int? DirtyFiles(StatusReport? status) => status?.Entries.Count(e => e.Kind != StatusEntryKind.Ignored);

    private static async Task<(int? Ahead, int? Behind)> AheadBehindOfEntryAsync(DescribeContext describe, WorktreeEntry entry)
    {
        if (!CanCountAheadBehind(describe, entry))
            return (null, null);

        var result = await describe.Repo.RunAsync(["rev-list", "--left-right", "--count", $"{describe.MainRef}...{entry.Head}"]).ConfigureAwait(false);
        var (behind, ahead) = ParseLeftRight(result);
        return (ahead, behind);
    }

    private static bool CanCountAheadBehind(DescribeContext describe, WorktreeEntry entry) =>
        describe.MainRef is not null && entry.Head is not null && !entry.IsPrunable;

    /// <summary>The two counts <c>rev-list --left-right --count</c> prints: commits only on the left, then only on the right.</summary>
    private static (int Left, int Right) ParseLeftRight(GitResult result)
    {
        var counts = result.StandardOutput.Split('\t', StringSplitOptions.TrimEntries);
        return (int.Parse(counts[0], CultureInfo.InvariantCulture), int.Parse(counts[1], CultureInfo.InvariantCulture));
    }

    private static DateTimeOffset? CommitTimeOf(WorktreeEntry entry, Dictionary<string, DateTimeOffset> commitTimes) =>
        entry.Head is { } head && commitTimes.TryGetValue(head, out var time) ? time : null;

    private static async Task<Dictionary<string, DateTimeOffset>> CommitTimesAsync(RepoCalls repo, List<string> commits)
    {
        if (commits.Count == 0)
            return [];

        List<string> args = ["log", "--no-walk=unsorted", "--format=%H %ct"];
        args.AddRange(commits);
        var result = await repo.RunAsync(args).ConfigureAwait(false);
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

    // fsck counts every reflog as a reference, including the stash reflog (stash@{1} and older) and
    // each worktree's HEAD reflog. What is left is referenced by nothing at all.
    // Ids are passed on the command line, so they go in batches to stay well under Windows' length limit.
    private static async Task<IReadOnlyList<UnreachableCommit>> FindUnreachableCommitsAsync(RepoCalls repo, Task<GitResult>? fsckTask)
    {
        if (fsckTask is null)
            return [];

        var ids = UnreachableParser.Parse((await fsckTask.ConfigureAwait(false)).StandardOutput)
            .Where(o => o.Type == "commit")
            .Select(o => o.Id)
            .Order(StringComparer.Ordinal)
            .ToList();

        var batches = await Task.WhenAll(ids.Chunk(UnreachableBatchSize).Select(async batch =>
        {
            List<string> args = ["log", "--no-walk=unsorted", "--format=%H%x1f%ct%x1f%s"];
            args.AddRange(batch);
            var result = await repo.RunAsync(args).ConfigureAwait(false);
            return result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\x1f', 3))
                .Select(p => new UnreachableCommit(p[0], DateTimeOffset.FromUnixTimeSeconds(long.Parse(p[1], CultureInfo.InvariantCulture)), p[2]));
        })).ConfigureAwait(false);

        return [.. batches.SelectMany(b => b).OrderBy(c => c.Id, StringComparer.Ordinal)];
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
                    CommitsAtRisk: isMerged ? 0 : risk.TryGetValue(r.Name, out var n) ? n : null)
                {
                    ObjectId = r.ObjectId,
                };
            })
            .ToList();

    private static bool IsOtherRef(string name) => name != Stash && !IsBranchOrTag(name);

    /// <summary>
    /// Folders git does not know as worktrees, in two places only:
    /// <list type="bullet">
    /// <item>Subfolders of worktree containers: the parent folders of linked worktrees, plus pattern paths with
    /// no wildcards below the repo root (such as .claude/worktrees). Listed when they have no .git entry, or a
    /// .git file that points into this repo's worktree records.</item>
    /// <item>Folders next to the repo. Only their .git file is read, and they are listed only when it points
    /// into this repo's worktree records. Nothing else in them is looked at.</item>
    /// </list>
    /// </summary>
    private List<LeftoverFolderInfo> FindLeftoverFolders(IReadOnlyList<WorktreeInfo> worktrees)
    {
        var registered = worktrees.Select(w => w.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var repoRoot = worktrees[0].Path;
        var records = WorktreeRecordsFolder(worktrees[0]);
        bool WasWorktree(string folder) =>
            records is not null && GitDirectory.ReadGitFileLink(folder) is { } link
            && string.Equals(Path.GetDirectoryName(link), records, StringComparison.OrdinalIgnoreCase);

        var inContainers = WorktreeContainers(worktrees, _options.WorktreeContainerPatterns)
            .SelectMany(SubfoldersOf)
            .Where(d => !registered.Contains(d) && (!GitDirectory.HasGitEntry(d) || WasWorktree(d)));
        var nextToRepo = (Path.GetDirectoryName(repoRoot) is { } parent ? SubfoldersOf(parent) : [])
            .Where(d => !registered.Contains(d) && WasWorktree(d));

        return inContainers.Concat(nextToRepo)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(d => new LeftoverFolderInfo(d, HoldsNoFiles(d), WasWorktree(d)))
            .ToList();
    }

    /// <summary>
    /// Folders that hold worktrees: the parent folders of linked worktrees, plus pattern paths with no wildcards
    /// below the repo root (such as .claude/worktrees). Only existing folders that match a pattern count.
    /// </summary>
    public static IReadOnlyList<string> WorktreeContainers(IReadOnlyList<WorktreeInfo> worktrees, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
            return [];

        var repoRoot = worktrees[0].Path;
        return [.. worktrees.Skip(1)
            .Select(w => Path.GetDirectoryName(w.Path))
            .OfType<string>()
            .Concat(patterns
                .Select(p => p.StartsWith("**/", StringComparison.Ordinal) ? p[3..] : null)
                .Where(p => p is not null && p.IndexOfAny(['*', '?']) < 0)
                .Select(p => NormalizePath(Path.Combine(repoRoot, p!))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(c => Directory.Exists(c) && GlobMatcher.IsMatchAny(patterns, c, ignoreCase: true))];
    }

    // <git dir>/worktrees, where git keeps one record folder per linked worktree.
    private static string? WorktreeRecordsFolder(WorktreeInfo main)
    {
        var gitDir = main.IsBare ? main.Path
            : Directory.Exists(Path.Combine(main.Path, ".git")) ? Path.Combine(main.Path, ".git")
            : GitDirectory.ReadGitFileLink(main.Path);
        return gitDir is null ? null : NormalizePath(Path.Combine(gitDir, "worktrees"));
    }

    private static IEnumerable<string> SubfoldersOf(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder).Select(NormalizePath).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Stops at the first file or link, so a folder full of files costs one directory read.
    private static bool HoldsNoFiles(string folder)
    {
        try
        {
            return !new DirectoryInfo(folder).EnumerateFileSystemInfos().Any(HoldsFiles);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HoldsFiles(FileSystemInfo entry) =>
        entry is FileInfo || entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || !HoldsNoFiles(entry.FullName);

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Runs git calls with a parallelism cap and records how long each one took.</summary>
    private sealed class CallContext(GitRunnerOptions runnerOptions, int maxParallel, CancellationToken ct)
    {
        private readonly SemaphoreSlim _gate = new(maxParallel);

        public ConcurrentQueue<GitCallTiming> Timings { get; } = new();

        public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> args) =>
            RunAsync(workingDirectory, args, []);

        public async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, IReadOnlyCollection<int> allowedExitCodes)
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

    /// <summary>Git calls that run in the repo's own folder.</summary>
    private sealed record RepoCalls(CallContext Call, string Path)
    {
        public Task<GitResult> RunAsync(IReadOnlyList<string> args) => Call.RunAsync(Path, args);
    }

    /// <summary>What describing each worktree in a full snapshot shares.</summary>
    private sealed record DescribeContext(RepoCalls Repo, string? MainRef, Task<Dictionary<string, DateTimeOffset>> CommitTimes);
}
