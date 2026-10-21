using System.Threading.Channels;
using Diffract.Fixtures;
using Diffract.Git.Snapshot;
using Diffract.Git.Watching;

namespace Diffract.Git.Tests.Watching;

/// <summary>
/// The watcher, the scheduler and the partial refresh together, on a fixture repo with real file events and real time.
/// Each test makes one change the way a tool would, and checks which refresh followed.
/// </summary>
public sealed class LiveRefreshTests : IAsyncDisposable
{
    private static readonly string[] ContainerPatterns = ["**/copilot-worktrees/*", "**/.claude/worktrees"];
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly TempDirectory _temp = new();
    private readonly Channel<(string Changes, string[] Calls)> _refreshes = Channel.CreateUnbounded<(string, string[])>();
    private readonly List<string> _events = [];
    private RepoSnapshotBuilder _builder = null!;
    private RefreshScheduler _scheduler = null!;
    private RepoWatcher _watcher = null!;
    private RepoSnapshot _snapshot = null!;
    private string _repo = null!;
    private string _worktree = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        _watcher?.Dispose();
        _scheduler?.Dispose();
        await Task.Delay(100);
        _temp.Dispose();
    }

    private async Task StartAsync()
    {
        _repo = (await new FixtureRepoBuilder(_temp).StaleAgentWorktreeAsync()).RepoPath;
        _worktree = Path.GetFullPath(Path.Combine(_repo, ".claude", "worktrees", "refactor-cart"));
        _builder = new RepoSnapshotBuilder(
            new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) },
            new SnapshotOptions { WorktreeContainerPatterns = ContainerPatterns, IncludeUnreachable = false });
        _snapshot = (await _builder.BuildAsync(_repo, Ct)).Snapshot;

        _scheduler = new RefreshScheduler(RefreshAsync, TimeProvider.System);
        _watcher = new RepoWatcher(WatchLayout.From(_snapshot, ContainerPatterns)!, change =>
        {
            lock (_events)
                _events.Add(change.ToString());
            _scheduler.Add(change);
        });
    }

    private async Task RefreshAsync(ChangeSet changes)
    {
        var result = await _builder.RefreshAsync(_snapshot, changes, CancellationToken.None);
        _snapshot = result.Snapshot;
        _watcher.Update(WatchLayout.From(_snapshot, ContainerPatterns)!);
        _refreshes.Writer.TryWrite((changes.ToString(), [.. result.Timings.Select(t => t.Command.Split(' ')[1]).Order(StringComparer.Ordinal)]));
    }

    private async Task<(string Changes, string[] Calls)> NextRefreshAsync() =>
        await _refreshes.Reader.ReadAsync(Ct).AsTask().WaitAsync(Patience, Ct);

    private Task<GitResult> GitAsync(string workingDirectory, params string[] args) =>
        IsolatedGit.WritableRunner(_temp, workingDirectory).RunAsync(args, Ct);

    private async Task AssertQuietAsync()
    {
        await Task.Delay(RefreshScheduler.MaxWait + TimeSpan.FromMilliseconds(500), Ct);
        Assert.False(_refreshes.Reader.TryRead(out var extra), $"Unexpected refresh: {extra.Changes}");
    }

    [Fact]
    public async Task A_file_written_in_a_worktree_refreshes_only_its_status()
    {
        await StartAsync();

        await File.WriteAllTextAsync(Path.Combine(_worktree, "notes.txt"), "todo", Ct);

        var refresh = await NextRefreshAsync();
        Assert.Equal($"files in {_worktree}", refresh.Changes);
        Assert.Equal(["status"], refresh.Calls);
        Assert.Equal(1, _snapshot.Worktrees.Single(w => w.Path == _worktree).DirtyFileCount);
    }

    [Fact]
    public async Task A_new_branch_refreshes_refs()
    {
        await StartAsync();

        await GitAsync(_repo, "branch", "copilot/new-idea");

        Assert.Equal("refs", (await NextRefreshAsync()).Changes);
        Assert.Contains(_snapshot.Branches, b => b.Name == "copilot/new-idea");
    }

    [Fact]
    public async Task A_commit_in_a_worktree_refreshes_its_head_and_the_refs()
    {
        await StartAsync();

        await File.WriteAllTextAsync(Path.Combine(_worktree, "cart.txt"), "v3", Ct);
        await GitAsync(_worktree, "commit", "-q", "-am", "Refactor cart again");

        var seen = new List<string>();
        while (_snapshot.Worktrees.Single(w => w.Path == _worktree).AheadOfMain != 3 || !seen.Any(c => c.Contains("refs")))
            seen.Add((await NextRefreshAsync()).Changes);
        Assert.Contains(seen, c => c.Contains($"HEAD of {_worktree}"));
        Assert.Equal(0, _snapshot.Worktrees.Single(w => w.Path == _worktree).DirtyFileCount);
    }

    [Fact]
    public async Task A_new_worktree_shows_up_and_is_then_watched()
    {
        await StartAsync();
        var added = Path.GetFullPath(Path.Combine(_repo, ".claude", "worktrees", "fix-login"));

        await GitAsync(_repo, "worktree", "add", "-q", "--detach", added);
        var seen = new List<string>();
        while (!_snapshot.Worktrees.Any(w => w.Path == added))
            seen.Add((await NextRefreshAsync()).Changes);
        Assert.Contains(seen, c => c.Contains("worktree list"));
        while (_refreshes.Reader.TryRead(out _)) { }
        await Task.Delay(RefreshScheduler.MaxWait, Ct);
        while (_refreshes.Reader.TryRead(out _)) { }

        await File.WriteAllTextAsync(Path.Combine(added, "login.txt"), "retry", Ct);

        Assert.Equal($"files in {added}", (await NextRefreshAsync()).Changes);
    }

    [Fact]
    public async Task A_removed_worktree_disappears()
    {
        await StartAsync();

        await GitAsync(_repo, "worktree", "remove", "--force", _worktree);

        while (_snapshot.Worktrees.Count > 1)
            await NextRefreshAsync();
        Assert.Single(_snapshot.Worktrees);
    }

    [Fact]
    public async Task A_leftover_folder_appearing_refreshes_the_worktree_list()
    {
        await StartAsync();
        var leftover = Path.GetFullPath(Path.Combine(_repo, ".claude", "worktrees", "gone-task"));

        Directory.CreateDirectory(leftover);

        Assert.Contains("worktree list", (await NextRefreshAsync()).Changes);
        Assert.Contains(_snapshot.LeftoverFolders, f => f.Path == leftover);
    }

    [Fact]
    public async Task A_refresh_never_causes_another_refresh()
    {
        await StartAsync();

        await File.WriteAllTextAsync(Path.Combine(_worktree, "notes.txt"), "todo", Ct);
        await NextRefreshAsync();

        await AssertQuietAsync();
    }

    [Fact]
    public async Task Diffract_own_git_calls_cause_no_file_events_at_all()
    {
        await StartAsync();
        await Task.Delay(500, Ct);
        lock (_events)
            _events.Clear();

        var withFsck = new RepoSnapshotBuilder(
            new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) },
            new SnapshotOptions { WorktreeContainerPatterns = ContainerPatterns, IncludeUnreachable = true });
        await withFsck.BuildAsync(_repo, Ct);
        await _builder.RefreshAsync(_snapshot, ChangeSet.All, Ct);

        await AssertQuietAsync();
        lock (_events)
            Assert.Empty(_events);
    }
}
