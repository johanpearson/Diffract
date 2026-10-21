using System.Collections.Concurrent;
using System.Globalization;
using Diffract.Git;
using Diffract.Git.Snapshot;
using Diffract.Leftovers;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar;

/// <summary>
/// The repos the UI has open, by id. Opening a repo that is already open, through any of its worktrees, returns the same id.
/// </summary>
internal sealed class RepoSessions(GitRunnerOptions runnerOptions, DiffractConfig config, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, RepoSession> _byId = new();
    private readonly Lock _openLock = new();
    private int _lastId;

    public int Count => _byId.Count;

    /// <summary>Builds a full snapshot, including unreachable commits, and starts tracking the repo.</summary>
    public async Task<RepoState> OpenAsync(string path, CancellationToken ct)
    {
        var snapshot = await BuildAsync(path, includeUnreachable: true, ct).ConfigureAwait(false);

        RepoSession session;
        lock (_openLock)
        {
            session = _byId.Values.FirstOrDefault(s => string.Equals(s.RepoRoot, snapshot.RepoRoot, StringComparison.OrdinalIgnoreCase))
                ?? Add(snapshot.RepoRoot);
        }

        await session.Refreshing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Remember(session, snapshot, unreachableCounted: true);
        }
        finally
        {
            session.Refreshing.Release();
        }
    }

    /// <summary>
    /// A full rebuild. Without <paramref name="includeUnreachable"/>, unreachable commits keep their last known value.
    /// One refresh runs per repo at a time; a second one waits for the first.
    /// </summary>
    public async Task<RepoState> RefreshAsync(string repoId, bool includeUnreachable, CancellationToken ct)
    {
        var session = Get(repoId);
        await session.Refreshing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = await BuildAsync(session.RepoRoot, includeUnreachable, ct).ConfigureAwait(false);
            return Remember(session, snapshot, includeUnreachable);
        }
        finally
        {
            session.Refreshing.Release();
        }
    }

    public void Close(string repoId)
    {
        if (!_byId.TryRemove(repoId, out _))
            throw UnknownRepo(repoId);
    }

    private RepoSession Add(string repoRoot)
    {
        var session = new RepoSession(Interlocked.Increment(ref _lastId).ToString(CultureInfo.InvariantCulture), repoRoot);
        _byId[session.Id] = session;
        return session;
    }

    private RepoSession Get(string repoId) => _byId.TryGetValue(repoId, out var session) ? session : throw UnknownRepo(repoId);

    private static RpcException UnknownRepo(string repoId) =>
        new(RpcErrorCodes.InvalidParams, $"No open repo has the id '{repoId}'. Open it first with repo.open.");

    private async Task<RepoSnapshot> BuildAsync(string path, bool includeUnreachable, CancellationToken ct)
    {
        var options = config.ToSnapshotOptions() with { IncludeUnreachable = includeUnreachable };
        var result = await new RepoSnapshotBuilder(runnerOptions, options).BuildAsync(path, ct).ConfigureAwait(false);
        return result.Snapshot;
    }

    private RepoState Remember(RepoSession session, RepoSnapshot snapshot, bool unreachableCounted)
    {
        if (unreachableCounted)
            (session.Unreachable, session.UnreachableCountedAt) = (snapshot.UnreachableCommits, snapshot.TakenAt);
        else
            snapshot = snapshot with { UnreachableCommits = session.Unreachable };

        var leftovers = new LeftoverClassifier().Classify(snapshot, config, time);
        return RepoState.From(session.Id, snapshot, leftovers, session.UnreachableCountedAt);
    }

    private sealed class RepoSession(string id, string repoRoot)
    {
        public string Id { get; } = id;

        public string RepoRoot { get; } = repoRoot;

        public SemaphoreSlim Refreshing { get; } = new(1, 1);

        public IReadOnlyList<UnreachableCommit> Unreachable { get; set; } = [];

        public DateTimeOffset UnreachableCountedAt { get; set; }
    }
}
