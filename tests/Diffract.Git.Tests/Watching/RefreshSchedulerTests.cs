using Diffract.Git.Snapshot;
using Diffract.Git.Watching;
using Microsoft.Extensions.Time.Testing;

namespace Diffract.Git.Tests.Watching;

/// <summary>The debounce, driven by a fake clock: nothing here waits for real time.</summary>
public sealed class RefreshSchedulerTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly List<string> _refreshes = [];
    private readonly RefreshScheduler _scheduler;
    private TaskCompletionSource? _hold;

    public RefreshSchedulerTests()
    {
        _scheduler = new RefreshScheduler(Refresh, _time);
    }

    public void Dispose() => _scheduler.Dispose();

    private Task Refresh(ChangeSet changes)
    {
        lock (_refreshes)
            _refreshes.Add(changes.ToString());
        return _hold?.Task ?? Task.CompletedTask;
    }

    private string[] Refreshes()
    {
        lock (_refreshes)
            return [.. _refreshes];
    }

    private void Advance(int milliseconds) => _time.Advance(TimeSpan.FromMilliseconds(milliseconds));

    [Fact]
    public void Refreshes_once_changes_have_been_quiet_for_300_ms()
    {
        _scheduler.Add(ChangeSet.RefsChanged);

        Advance(299);
        Assert.Empty(Refreshes());
        Advance(1);
        Assert.Equal(["refs"], Refreshes());
    }

    [Fact]
    public void Each_change_restarts_the_quiet_period()
    {
        _scheduler.Add(ChangeSet.RefsChanged);
        Advance(200);
        _scheduler.Add(ChangeSet.FilesChanged(@"C:\src\shop"));
        Advance(200);

        Assert.Empty(Refreshes());
        Advance(100);
        Assert.Equal([@"refs, files in C:\src\shop"], Refreshes());
    }

    [Fact]
    public void Refreshes_at_least_every_2_seconds_while_changes_keep_coming()
    {
        for (var elapsed = 0; elapsed < 1900; elapsed += 100)
        {
            _scheduler.Add(ChangeSet.FilesChanged(@"C:\src\shop"));
            Advance(100);
        }
        Assert.Empty(Refreshes());

        Advance(100);

        Assert.Single(Refreshes());
    }

    [Fact]
    public void Changes_during_a_refresh_go_into_the_next_one_and_never_overlap()
    {
        _hold = new TaskCompletionSource();
        _scheduler.Add(ChangeSet.RefsChanged);
        Advance(300);

        _scheduler.Add(ChangeSet.FilesChanged(@"C:\src\a"));
        _scheduler.Add(ChangeSet.FilesChanged(@"C:\src\b"));
        Advance(5000);
        Assert.Equal(["refs"], Refreshes());

        var next = _hold;
        _hold = null;
        next.SetResult();

        Advance(0);
        Assert.Equal(["refs", @"files in C:\src\a, files in C:\src\b"], Refreshes());
    }

    [Fact]
    public void A_failed_refresh_does_not_stop_later_ones()
    {
        var scheduler = new RefreshScheduler(_ => throw new InvalidOperationException("git broke"), _time);
        var finished = 0;
        scheduler.RefreshFinished += () => finished++;

        scheduler.Add(ChangeSet.RefsChanged);
        Advance(300);
        scheduler.Add(ChangeSet.RefsChanged);
        Advance(300);

        Assert.Equal(2, finished);
        scheduler.Dispose();
    }

    [Fact]
    public void An_empty_change_schedules_nothing()
    {
        _scheduler.Add(ChangeSet.None);
        Advance(5000);

        Assert.Empty(Refreshes());
    }

    [Fact]
    public void Nothing_runs_after_dispose()
    {
        _scheduler.Add(ChangeSet.RefsChanged);
        _scheduler.Dispose();
        Advance(5000);

        Assert.Empty(Refreshes());
    }
}
