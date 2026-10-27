using System.Collections.Concurrent;
using System.Globalization;
using Diffract.Git;
using Diffract.Git.Snapshot;
using Diffract.Git.Watching;
using Diffract.Leftovers;
using Diffract.Leftovers.Cleanup;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar;

/// <summary>What the refresh after a cleanup counts. <c>git fsck</c> is slow, so only when commits may have stopped, or started, being orphaned.</summary>
internal enum AfterCleanup { Refresh, RecountUnreachable }

/// <summary>
/// The repos the UI has open, by id. Opening a repo that is already open, through any of its worktrees, returns the same id.
/// Opening answers with a quick snapshot, which is all history needs, and scans the rest in the background: worktree
/// state, leftovers and unreachable commits, <c>fsck</c> included. The scan's result is sent as <c>repo.changed</c>.
/// From then on the repo is watched: changes refresh only the part of the snapshot they touch, and each refresh is
/// sent as <c>repo.changed</c>.
/// </summary>
/// <param name="notify">Sends a <c>repo.changed</c> notification.</param>
/// <param name="log">Where failed live refreshes are reported. They have no request to answer.</param>
/// <param name="paused">While true, live refreshes are skipped, so Diffract starts no git. Opening the repo again catches up.</param>
internal sealed class RepoSessions(
    GitRunnerOptions runnerOptions, DiffractConfig config, TimeProvider time, Func<RepoChanged, Task> notify, TextWriter log, Func<bool>? paused = null)
    : IDisposable
{
    private readonly ConcurrentDictionary<string, RepoSession> _byId = new();

    /// <summary>The rows the user hid, which the state lists apart and the counts leave out.</summary>
    public HiddenRows Hidden { get; init; } = new(HiddenRows.DefaultFolder);
    private readonly Lock _openLock = new();
    private int _lastId;

    public int Count => _byId.Count;

    /// <summary>
    /// Answers with a quick snapshot and starts the scan, unless the repo is already open and scanned: then it answers
    /// with what it knows, which the watcher keeps current.
    /// </summary>
    public async Task<RepoState> OpenAsync(string path, CancellationToken ct)
    {
        var quick = await Builder(includeUnreachable: false).BuildQuickAsync(path, ct).ConfigureAwait(false);

        RepoSession session;
        lock (_openLock)
        {
            session = _byId.Values.FirstOrDefault(s => string.Equals(s.RepoRoot, quick.Snapshot.RepoRoot, StringComparison.OrdinalIgnoreCase))
                ?? Add(quick.Snapshot.RepoRoot);
        }

        await session.Refreshing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!session.Scanned)
            {
                session.Snapshot = quick.Snapshot;
                session.StartScan(() => ScanAsync(session));
            }
            return StateOf(session);
        }
        finally
        {
            session.Refreshing.Release();
        }
    }

    /// <summary>Completes when the scan after opening has finished and its <c>repo.changed</c> is sent, or failed.</summary>
    public Task ScannedAsync(string repoId) => Get(repoId).Scan ?? Task.CompletedTask;

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
        var session = Get(repoId);
        if (_byId.TryRemove(repoId, out _))
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

    /// <summary>The repo's latest snapshot. History reads its refs and worktree HEADs from it, so it starts no git.</summary>
    public RepoSnapshot SnapshotOf(string repoId) =>
        Get(repoId).Snapshot ?? throw new InvalidOperationException("The repo is still being opened.");

    /// <summary>The state the UI has for the repo, hidden rows as they are now included. Starts no git.</summary>
    public RepoState CurrentState(RepoSession session) => StateOf(session);

    /// <summary>The latest full snapshot and all its leftovers, hidden ones too. Refused before the scan after opening has finished.</summary>
    public (RepoSnapshot Snapshot, IReadOnlyList<Leftover> Leftovers) LeftoversOf(RepoSession session) =>
        session is { Scanned: true, Snapshot: { } snapshot }
            ? (snapshot, new LeftoverClassifier().Classify(snapshot, config, time))
            : throw new CleanupLogException("The repo is still being scanned. Clean up once its leftovers are listed.");

    /// <summary>
    /// Runs a cleanup or its undo with the repo's live refresh paused, so Diffract does not read the repo half changed,
    /// then catches up with one full refresh, sent as <c>repo.changed</c> before this returns. One at a time per repo.
    /// </summary>
    /// <param name="after">What the refresh afterwards counts: unreachable commits too, as after keeping orphaned commits.</param>
    public async Task<T> CleanUpAsync<T>(RepoSession session, AfterCleanup after, Func<string, Task<T>> run)
    {
        if (!session.TryStartCleanup())
            throw new CleanupLogException("A cleanup is already running in this repo. Wait for it to finish.");
        try
        {
            return await run(session.RepoRoot).ConfigureAwait(false);
        }
        finally
        {
            session.EndCleanup();
            await CatchUpAsync(session, after).ConfigureAwait(false);
        }
    }

    /// <summary>A failure is logged, not thrown: the cleanup's own result matters more, and the next change tries again.</summary>
    private async Task CatchUpAsync(RepoSession session, AfterCleanup after)
    {
        try
        {
            var state = await RefreshAsync(session.Id, after == AfterCleanup.RecountUnreachable, session.Closing).ConfigureAwait(false);
            await notify(new RepoChanged([new RepoChange(ChangeKind.Everything, null)], state)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await log.WriteLineAsync($"Refresh of {session.RepoRoot} after a cleanup failed: {e.Message}").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Closed meanwhile: nobody is listening.
        }
    }

    /// <summary>The open repo with that id. The cleanup methods hold on to it for the length of a request.</summary>
    public RepoSession Get(string repoId) =>
        _byId.TryGetValue(repoId, out var session)
            ? session
            : throw new RpcException(RpcErrorCodes.InvalidParams, $"No open repo has the id '{repoId}'. Open it first with repo.open.");

    private RepoSnapshotBuilder Builder(bool includeUnreachable) =>
        new(runnerOptions, config.ToSnapshotOptions() with { IncludeUnreachable = includeUnreachable });

    /// <summary>The full snapshot, <c>fsck</c> included, sent as <c>repo.changed</c>. A failure is sent too, with why.</summary>
    private async Task ScanAsync(RepoSession session)
    {
        var ct = session.Closing;
        try
        {
            var state = await ScanOnceAsync(session, ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested)
                await notify(new RepoChanged([new RepoChange(ChangeKind.Scanned, null)], state)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task<RepoState> ScanOnceAsync(RepoSession session, CancellationToken ct)
    {
        await session.Refreshing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var full = await Builder(includeUnreachable: true).BuildAsync(session.RepoRoot, ct).ConfigureAwait(false);
            return Remember(session, full.Snapshot, unreachableCounted: true);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            session.ScanProblem = e.Message;
            await log.WriteLineAsync($"Scan of {session.RepoRoot} failed: {e.Message}").ConfigureAwait(false);
            return StateOf(session);
        }
        finally
        {
            session.Refreshing.Release();
        }
    }

    private async Task LiveRefreshAsync(RepoSession session, ChangeSet changes)
    {
        if (paused?.Invoke() == true || session.IsCleaningUp)
        {
            await log.WriteLineAsync($"Live refresh of {session.RepoRoot} ({changes}) skipped while git is being updated or the repo cleaned up.").ConfigureAwait(false);
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

    /// <summary>
    /// Keeps a full snapshot for the next partial refresh, and points the watcher at its worktrees. Any full snapshot
    /// counts as the scan, so a refresh after a failed scan puts things right.
    /// </summary>
    private RepoState Remember(RepoSession session, RepoSnapshot snapshot, bool unreachableCounted)
    {
        if (unreachableCounted)
            (session.Unreachable, session.UnreachableCountedAt) = (snapshot.UnreachableCommits, snapshot.TakenAt);
        else
            snapshot = snapshot with { UnreachableCommits = session.Unreachable };

        session.Snapshot = snapshot;
        (session.Scanned, session.ScanProblem) = (true, null);
        session.Watch(WatchLayout.From(snapshot, config.WorktreeContainerPatterns));
        return StateOf(session);
    }

    /// <summary>The state to send. Leftovers only from a full snapshot: a quick one lacks what the rules need.</summary>
    private RepoState StateOf(RepoSession session)
    {
        var snapshot = session.Snapshot!;
        var rows = session.Scanned ? Hidden.For(session.RepoRoot).Split(new LeftoverClassifier().Classify(snapshot, config, time)) : new SplitRows([], []);
        var scanning = !session.Scanned && session.ScanProblem is null;
        return RepoState.From(session.Id, snapshot, rows, new ScanStatus(session.UnreachableCountedAt, scanning, session.ScanProblem));
    }

    internal sealed class RepoSession(string id, string repoRoot) : IDisposable
    {
        private readonly CancellationTokenSource _closing = new();
        private RepoWatcher? _watcher;

        public string Id { get; } = id;

        public string RepoRoot { get; } = repoRoot;

        public SemaphoreSlim Refreshing { get; } = new(1, 1);

        /// <summary>1 while a cleanup or undo runs, which pauses live refresh.</summary>
        private int _cleaningUp;

        public bool IsCleaningUp => Volatile.Read(ref _cleaningUp) == 1;

        /// <summary>False when one is running already.</summary>
        public bool TryStartCleanup() => Interlocked.Exchange(ref _cleaningUp, 1) == 0;

        public void EndCleanup() => Volatile.Write(ref _cleaningUp, 0);

        public RefreshScheduler? Scheduler { get; set; }

        public RepoSnapshot? Snapshot { get; set; }

        public IReadOnlyList<UnreachableCommit> Unreachable { get; set; } = [];

        public DateTimeOffset? UnreachableCountedAt { get; set; }

        /// <summary>A full snapshot has been taken: by the scan after opening, or by a refresh.</summary>
        public bool Scanned { get; set; }

        /// <summary>Why the last scan failed. Cleared by the next full snapshot.</summary>
        public string? ScanProblem { get; set; }

        /// <summary>The scan after opening, the last one started. Null before the first open.</summary>
        public Task? Scan { get; private set; }

        /// <summary>Starts the scan, unless one is running. Opening again after a failed scan tries again.</summary>
        public void StartScan(Func<Task> scan)
        {
            if (Scan is { IsCompleted: false })
                return;
            ScanProblem = null;
            Scan = Task.Run(scan);
        }

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
