using Diffract.Git.Parsing;

namespace Diffract.Git.History;

/// <summary>How a history read from some tips changes when it is read from others.</summary>
/// <param name="Added">
/// Commits reachable from the new tips and from none of the old ones, in topological order. None of them is an
/// ancestor of a commit already read, so they all go above it.
/// </param>
/// <param name="Removed">Commits reachable from the old tips and from none of the new ones, such as those a force-move left behind.</param>
/// <param name="TooMany">
/// More than <see cref="HistoryUpdater.MaxCommits"/> were added or removed, so reading the whole history again is
/// cheaper. <see cref="Added"/> and <see cref="Removed"/> are then empty.
/// </param>
public sealed record HistoryChange(IReadOnlyList<CommitEntry> Added, IReadOnlyList<string> Removed, bool TooMany)
{
    public static HistoryChange None { get; } = new([], [], false);

    public static HistoryChange TooManyChanges { get; } = new([], [], true);
}

/// <summary>
/// Reads only what changed in a history when its tips move: one git call for the new commits, and one more only when
/// a tip was moved back or removed rather than moved forward.
/// </summary>
public sealed class HistoryUpdater
{
    /// <summary>Two pages of commits. A larger change, such as a first fetch, reads the whole history again.</summary>
    public const int MaxCommits = 2 * HistoryReader.PageSize;

    private static readonly IReadOnlyList<string> RevListArguments = ["rev-list", "--stdin", "--"];

    private readonly GitRunnerOptions _runnerOptions;
    private readonly int _maxCommits;

    /// <param name="maxCommits">Tests use a small limit.</param>
    public HistoryUpdater(GitRunnerOptions runnerOptions, int maxCommits = MaxCommits)
    {
        if (!runnerOptions.ReadOnly)
            throw new ArgumentException("The history updater only runs with a read-only git runner.", nameof(runnerOptions));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCommits, 1);
        _runnerOptions = runnerOptions;
        _maxCommits = maxCommits;
    }

    /// <summary>What changes when a history read from <paramref name="oldTips"/> is read from <paramref name="newTips"/>.</summary>
    public async Task<HistoryChange> UpdateAsync(string repoRoot, IReadOnlyCollection<string> oldTips, IReadOnlyCollection<string> newTips, CancellationToken ct)
    {
        var old = Ids(oldTips);
        var current = Ids(newTips);
        if (old.SetEquals(current))
            return HistoryChange.None;

        var added = current.IsSubsetOf(old) ? [] : await AddedAsync(repoRoot, current, old, ct).ConfigureAwait(false);
        if (added is null)
            return HistoryChange.TooManyChanges;

        // A tip that moved forward is a parent of a new commit, so everything it reached is still there.
        var gone = old.Except(current).Except(added.SelectMany(c => c.ParentIds)).ToList();
        var removed = gone.Count == 0 ? [] : await RemovedAsync(repoRoot, gone, current, ct).ConfigureAwait(false);
        return removed is null ? HistoryChange.TooManyChanges : new HistoryChange(added, removed, false);
    }

    private async Task<IReadOnlyList<CommitEntry>?> AddedAsync(string repoRoot, IEnumerable<string> tips, IEnumerable<string> excluded, CancellationToken ct)
    {
        IReadOnlyList<CommitEntry>? added = null;
        await Runner(repoRoot).StreamAsync(new GitCommandLine(CommitLogParser.Arguments, CommitLogParser.Input(tips, excluded)), async (output, token) =>
        {
            added = await CommitLogParser.ReadAtMostAsync(output, _maxCommits, token).ConfigureAwait(false);
            return added is not null;
        }, ct).ConfigureAwait(false);
        return added;
    }

    private async Task<IReadOnlyList<string>?> RemovedAsync(string repoRoot, IEnumerable<string> tips, IEnumerable<string> excluded, CancellationToken ct)
    {
        List<string> removed = [];
        var complete = false;
        await Runner(repoRoot).StreamAsync(new GitCommandLine(RevListArguments, CommitLogParser.Input(tips, excluded)), async (output, token) =>
        {
            complete = await ReadIdsAsync(output, removed, token).ConfigureAwait(false);
            return complete;
        }, ct).ConfigureAwait(false);
        return complete ? removed : null;
    }

    /// <summary>Reads one id per line into <paramref name="ids"/>. False as soon as there are more than the limit.</summary>
    private async Task<bool> ReadIdsAsync(Stream output, List<string> ids, CancellationToken ct)
    {
        // The runner owns the stream and drains it afterwards, so the reader leaves it open.
        using var reader = new StreamReader(output, leaveOpen: true);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { Length: > 0 } line)
        {
            if (ids.Count == _maxCommits)
                return false;
            ids.Add(ObjectId.Parse(line));
        }
        return true;
    }

    private static HashSet<string> Ids(IEnumerable<string> ids) => ids.Select(ObjectId.Require).ToHashSet(StringComparer.Ordinal);

    private GitRunner Runner(string repoRoot) => new(repoRoot, _runnerOptions);
}
