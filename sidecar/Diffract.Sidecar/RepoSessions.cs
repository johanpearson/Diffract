using System.Collections.Concurrent;
using System.Globalization;
using Diffract.Git;
using Diffract.Git.Snapshot;
using Diffract.Git.Watching;
using Diffract.Leftovers;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar;

/// <summary>
/// The repos the UI has open, by id. Opening a repo that is already open, through any of its worktrees, returns the same id.
/// Each open repo is watched: changes refresh only the part of the snapshot they touch, and each refresh is sent as
/// <c>repo.changed</c>.
/// </summary>
/// <param name="notify">Sends a <c>repo.changed</c> notification.</param>
/// <param name="log">Where failed live refreshes are reported. They have no request to answer.</param>
/// <param name="paused">While true, live refreshes are skipped, so Diffract starts no git. Opening the repo again catches up.</param>
internal sealed class RepoSessions(
    GitRunnerOptions runnerOptions, DiffractConfig config, TimeProvider time, Func<RepoChanged, Task> notify, TextWriter log, Func<bool>? paused = null)
    : IDisposable
{
    private readonly ConcurrentDictionary<string, RepoSession> _byId = new();
    private readonly Lock _openLock = new();
    private int _lastId;

    public int Count => _byId.Count;

    /// <summary>Builds a full snapshot, including unreachable commits, and starts watching the repo.</summary>
    public async Task<RepoState> OpenAsync(string path, CancellationToken ct)
    {
        var snapshot = await Builder(includeUnreachable: true).BuildAsync(path, ct).ConfigureAwait(false);

        RepoSession session;
        lock (_openLock)
        {
            session = _byId.Values.FirstOrDefault(s => string.Equals(s.RepoRoot, snapshot.Snapshot.RepoRoot, StringComparison.OrdinalIgnoreCase))
                ?? Add(snapshot.Snapshot.RepoRoot);
        }

        await session.Refreshing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Remember(session, snapshot.Snapshot, unreachableCounted: true);
        }
        finally
        {
            session.Refreshing.Release();
        }
    }

    /// <summary>
    /// A full rebuild. Without <paramref name="includeUnreachable"/>, unreachable commits keep their last known value.
    /// One refresh runs per repo at a time, live ones included; a second one waits for the first.
    /// </summary>
    public async Task<RepoState> RefreshAsync(string repoId, bool includeUnreachable, CancellationToken ct)
    {
        var session = Get(repoId);
        await session.Refreshing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = await Builder(includeUnreachable).BuildAsync(session.RepoRoot, ct).ConfigureAwait(false);
            return Remember(session, snapshot.Snapshot, includeUnreachable);
        }
        finally
        {
            session.Refreshing.Release();
        }
    }

    /// <summary>Stops watching. A live refresh that is running is cancelled and sends nothing.</summary>
    public void Close(string repoId)
    {
        if (!_byId.TryRemove(repoId, out var session))
            throw UnknownRepo(repoId);
        session.Dispose();
    }

    public void Dispose()
    {
        foreach (var id in _byId.Keys.ToList())
        {
            if (_byId.TryRemove(id, out var session))
                session.Dispose();
        }
    }

    private RepoSession Add(string repoRoot)
    {
        var session = new RepoSession(Interlocked.Increment(ref _lastId).ToString(CultureInfo.InvariantCulture), repoRoot);
        session.Scheduler = new RefreshScheduler(changes => LiveRefreshAsync(session, changes), time);
        _byId[session.Id] = session;
        return session;
    }

    private RepoSession Get(string repoId) => _byId.TryGetValue(repoId, out var session) ? session : throw UnknownRepo(repoId);

    private static RpcException UnknownRepo(string repoId) =>
        new(RpcErrorCodes.InvalidParams, $"No open repo has the id '{repoId}'. Open it first with repo.open.");

    private RepoSnapshotBuilder Builder(bool includeUnreachable) =>
        new(runnerOptions, config.ToSnapshotOptions() with { IncludeUnreachable = includeUnreachable });

    private async Task LiveRefreshAsync(RepoSession session, ChangeSet changes)
    {
        if (paused?.Invoke() == true)
        {
            await log.WriteLineAsync($"Live refresh of {session.RepoRoot} ({changes}) skipped while git is being updated.").ConfigureAwait(false);
            return;
        }

        var ct = session.Closing;
        try
        {
            await session.Refreshing.WaitAsync(ct).ConfigureAwait(false);
            RepoState state;
            try
            {
                var result = await Builder(includeUnreachable: false).RefreshAsync(session.Snapshot!, changes, ct).ConfigureAwait(false);
                state = Remember(session, result.Snapshot, unreachableCounted: false);
            }
            finally
            {
                session.Refreshing.Release();
            }

            if (!ct.IsCancellationRequested)
                await notify(new RepoChanged(RepoChange.From(changes), state)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            // The next change tries again. If the repo itself is gone, the UI learns it on its next request.
            await log.WriteLineAsync($"Live refresh of {session.RepoRoot} ({changes}) failed: {e.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>Keeps the snapshot for the next partial refresh, and points the watcher at its worktrees.</summary>
    private RepoState Remember(RepoSession session, RepoSnapshot snapshot, bool unreachableCounted)
    {
        if (unreachableCounted)
            (session.Unreachable, session.UnreachableCountedAt) = (snapshot.UnreachableCommits, snapshot.TakenAt);
        else
            snapshot = snapshot with { UnreachableCommits = session.Unreachable };

        session.Snapshot = snapshot;
        session.Watch(WatchLayout.From(snapshot, config.WorktreeContainerPatterns));

        var leftovers = new LeftoverClassifier().Classify(snapshot, config, time);
        return RepoState.From(session.Id, snapshot, leftovers, session.UnreachableCountedAt);
    }

    private sealed class RepoSession(string id, string repoRoot) : IDisposable
    {
        private readonly CancellationTokenSource _closing = new();
        private RepoWatcher? _watcher;

        public string Id { get; } = id;

        public string RepoRoot { get; } = repoRoot;

        public SemaphoreSlim Refreshing { get; } = new(1, 1);

        public RefreshScheduler? Scheduler { get; set; }

        public RepoSnapshot? Snapshot { get; set; }

        public IReadOnlyList<UnreachableCommit> Unreachable { get; set; } = [];

        public DateTimeOffset UnreachableCountedAt { get; set; }

        public CancellationToken Closing => _closing.Token;

        /// <summary>Starts watching, or moves the watcher to the current worktrees. Null when .git cannot be found.</summary>
        public void Watch(WatchLayout? layout)
        {
            if (layout is null || _closing.IsCancellationRequested)
                return;
            if (_watcher is null)
                _watcher = new RepoWatcher(layout, change => Scheduler?.Add(change));
            else
                _watcher.Update(layout);
        }

        public void Dispose()
        {
            _closing.Cancel();
            _watcher?.Dispose();
            Scheduler?.Dispose();
        }
    }
}
