using Diffract.Git.Files;
using Diffract.Git.Parsing;
using Diffract.Git.Snapshot;

namespace Diffract.Git.History;

/// <summary>
/// Reads a repo's history with as few git calls as possible: one streamed call for the whole list, one for a
/// commit's details, and one for each side of a file's diff.
/// </summary>
public sealed class HistoryReader
{
    /// <summary>Commits per page sent to the UI. The first page goes out as soon as it is parsed.</summary>
    public const int PageSize = 500;

    /// <summary>Larger files are refused rather than sent whole.</summary>
    public const int MaxFileBytes = FileContentReader.MaxFileBytes;

    private readonly GitRunnerOptions _runnerOptions;
    private readonly BlobReader _blobs;
    private readonly int _pageSize;

    /// <param name="pageSize">Commits per page that <see cref="ReadAsync"/> hands on. Tests use small pages.</param>
    public HistoryReader(GitRunnerOptions runnerOptions, int pageSize = PageSize)
    {
        if (!runnerOptions.ReadOnly)
            throw new ArgumentException("The history reader only runs with a read-only git runner.", nameof(runnerOptions));
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _runnerOptions = runnerOptions;
        _blobs = new BlobReader(runnerOptions);
        _pageSize = pageSize;
    }

    /// <summary>
    /// The commits history starts from by default: what local branches, remote-tracking branches and tags point at,
    /// and every worktree's HEAD. A detached HEAD is the only one not already among them. Each commit once.
    /// </summary>
    public static IReadOnlyList<string> TipsOf(RepoSnapshot snapshot) =>
    [
        .. snapshot.Branches.Select(b => b.TargetId)
            .Concat(snapshot.Tags.Select(t => t.TargetId))
            .Concat(snapshot.Worktrees.Select(w => w.Head).OfType<string>())
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Reads the history from <paramref name="tips"/> in topological order with one git call, handing it on page by
    /// page as it streams. No tips, as in an empty repo, need no git at all.
    /// </summary>
    /// <returns>How many commits were read.</returns>
    public async Task<int> ReadAsync(string repoRoot, IReadOnlyCollection<string> tips, Func<IReadOnlyList<CommitEntry>, Task> onPage, CancellationToken ct)
    {
        if (tips.Count == 0)
            return 0;

        var total = 0;
        await Runner(repoRoot).StreamAsync(new GitCommandLine(CommitLogParser.Arguments, CommitLogParser.Input(tips)), async (output, token) =>
        {
            total = await CommitLogParser.ReadPagesAsync(output, _pageSize, onPage, token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
        return total;
    }

    public async Task<CommitDetails> ReadDetailsAsync(string repoRoot, string commitId, CancellationToken ct)
    {
        var result = await Runner(repoRoot).RunAsync(CommitDetailsParser.Arguments(commitId), ct).ConfigureAwait(false);
        return CommitDetailsParser.Parse(result.StandardOutputBytes);
    }

    /// <summary>
    /// Reads one blob, stopping git as soon as it is clearly binary or larger than <paramref name="maxBytes"/>.
    /// <c>cat-file</c> hands over the stored bytes, so no filter or textconv from the repo's config ever runs.
    /// </summary>
    public Task<FileContent> ReadFileAsync(string repoRoot, string blobId, CancellationToken ct, int maxBytes = MaxFileBytes) =>
        _blobs.ReadAsync(repoRoot, blobId, ct, maxBytes);

    private GitRunner Runner(string repoRoot) => new(repoRoot, _runnerOptions);
}
