using Diffract.Git.Snapshot;

namespace Diffract.Git.Watching;

/// <summary>
/// Collects changes and runs one refresh for a burst of them: once changes have been quiet for <see cref="Quiet"/>,
/// or <see cref="MaxWait"/> after the first one if they keep coming. At most one refresh runs at a time;
/// changes that arrive meanwhile are merged into the next one.
/// </summary>
public sealed class RefreshScheduler : IDisposable
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(2);

    private readonly Func<ChangeSet, Task> _refresh;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly Lock _lock = new();
    private ChangeSet _pending = ChangeSet.None;
    private DateTimeOffset _firstAt;
    private DateTimeOffset _lastAt;
    private bool _running;
    private bool _disposed;

    /// <param name="refresh">Runs one refresh. Exceptions are the caller's to log; they never stop the scheduler.</param>
    public RefreshScheduler(Func<ChangeSet, Task> refresh, TimeProvider time)
    {
        _refresh = refresh;
        _time = time;
        _timer = time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised after each refresh, whether it worked or not.</summary>
    public event Action? RefreshFinished;

    public void Add(ChangeSet change)
    {
        if (change.IsEmpty)
            return;

        lock (_lock)
        {
            if (_disposed)
                return;
            var now = _time.GetUtcNow();
            if (_pending.IsEmpty)
                _firstAt = now;
            _lastAt = now;
            _pending = _pending.Merge(change);
            if (!_running)
                Arm(now);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _pending = ChangeSet.None;
        }
        _timer.Dispose();
    }

    private DateTimeOffset Due => Min(_lastAt + Quiet, _firstAt + MaxWait);

    /// <summary>Whether a refresh may start now. Read under the lock.</summary>
    private bool CanStart => !_disposed && !_running && !_pending.IsEmpty;

    private void Arm(DateTimeOffset now)
    {
        var wait = Due - now;
        _timer.Change(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer()
    {
        ChangeSet batch;
        lock (_lock)
        {
            if (!CanStart)
                return;

            // A change arrived after this timer was set, which pushed the quiet deadline back.
            var now = _time.GetUtcNow();
            if (now < Due)
            {
                Arm(now);
                return;
            }

            (batch, _pending, _running) = (_pending, ChangeSet.None, true);
        }

        _ = RunAsync(batch);
    }

    private async Task RunAsync(ChangeSet batch)
    {
        try
        {
            await _refresh(batch).ConfigureAwait(false);
        }
        catch
        {
            // The refresh callback logs its own failures. The next change gets a fresh try.
        }
        finally
        {
            lock (_lock)
            {
                _running = false;
                if (!_disposed && !_pending.IsEmpty)
                    Arm(_time.GetUtcNow());
            }
            RefreshFinished?.Invoke();
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
