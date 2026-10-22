using System.Collections.Concurrent;
using Diffract.Fixtures;
using Diffract.Git.History;
using Diffract.Git.Snapshot;

namespace Diffract.Git.Tests.History;

/// <summary>
/// The history updater against real fixture repos: what a branch moving, appearing or disappearing adds to and
/// removes from a history already read, and how many git processes that takes.
/// </summary>
public sealed class HistoryUpdaterTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConcurrentQueue<GitCall> _calls = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GitRunnerOptions Options => new() { Environment = IsolatedGit.Environment(_temp), OnCall = _calls.Enqueue };

    private HistoryUpdater Updater(int maxCommits = HistoryUpdater.MaxCommits) => new(Options, maxCommits);

    /// <summary>main with two commits, and feature with one more.</summary>
    private async Task<FixtureRepo> RepoAsync()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        await repo.CommitAsync("file.txt", "1", "First");
        await repo.CommitAsync("file.txt", "2", "Second");
        await repo.GitAsync("switch", "-q", "-c", "feature");
        await repo.CommitAsync("feature.txt", "1", "Feature");
        await repo.GitAsync("switch", "-q", "main");
        return repo;
    }

    private async Task<IReadOnlyList<string>> TipsAsync(FixtureRepo repo) =>
        HistoryReader.TipsOf((await new RepoSnapshotBuilder(new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) }, new SnapshotOptions())
            .BuildQuickAsync(repo.Path, Ct)).Snapshot);

    private async Task<HistoryChange> UpdateAsync(FixtureRepo repo, IReadOnlyList<string> oldTips, int maxCommits = HistoryUpdater.MaxCommits)
    {
        var newTips = await TipsAsync(repo);
        _calls.Clear();
        return await Updater(maxCommits).UpdateAsync(repo.Path, oldTips, newTips, Ct);
    }

    [Fact]
    public void Refuses_a_runner_that_may_write()
    {
        Assert.Throws<ArgumentException>(() => new HistoryUpdater(new GitRunnerOptions { ReadOnly = false }));
    }

    [Fact]
    public async Task Unchanged_tips_change_nothing_and_need_no_git()
    {
        var repo = await RepoAsync();
        var tips = await TipsAsync(repo);

        var change = await UpdateAsync(repo, tips);

        Assert.Same(HistoryChange.None, change);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task A_new_commit_on_a_branch_is_added_with_one_git_call()
    {
        var repo = await RepoAsync();
        var before = await TipsAsync(repo);
        await repo.CommitAsync("file.txt", "3", "Third");
        await repo.CommitAsync("file.txt", "4", "Fourth");

        var change = await UpdateAsync(repo, before);

        Assert.Equal(["Fourth", "Third"], change.Added.Select(c => c.Subject));
        Assert.Empty(change.Removed);
        Assert.False(change.TooMany);
        Assert.Single(_calls);
    }

    [Fact]
    public async Task A_new_branch_on_a_commit_already_read_adds_nothing_with_one_git_call()
    {
        var repo = await RepoAsync();
        var before = await TipsAsync(repo);
        await repo.GitAsync("branch", "other", "HEAD~1");

        var change = await UpdateAsync(repo, before);

        Assert.Empty(change.Added);
        Assert.Empty(change.Removed);
        Assert.Single(_calls);
    }

    [Fact]
    public async Task A_new_branch_with_its_own_commits_adds_them()
    {
        var repo = await RepoAsync();
        var before = await TipsAsync(repo);
        await repo.GitAsync("switch", "-q", "-c", "other", "HEAD~1");
        await repo.CommitAsync("other.txt", "1", "Other work");

        var change = await UpdateAsync(repo, before);

        Assert.Equal(["Other work"], change.Added.Select(c => c.Subject));
        Assert.Empty(change.Removed);
    }

    [Theory]
    [InlineData("branch", "-f", "feature", "main")]
    [InlineData("branch", "-q", "-D", "feature")]
    public async Task A_branch_moved_back_or_deleted_removes_the_commits_only_it_had_with_one_git_call(params string[] move)
    {
        var repo = await RepoAsync();
        var feature = await repo.RevParseAsync("feature");
        var before = await TipsAsync(repo);
        await repo.GitAsync(move);

        var change = await UpdateAsync(repo, before);

        Assert.Empty(change.Added);
        Assert.Equal([feature], change.Removed);
        Assert.Single(_calls);
    }

    [Fact]
    public async Task A_branch_force_moved_to_a_rewrite_adds_the_new_commits_and_removes_the_old_ones()
    {
        var repo = await RepoAsync();
        var oldFeature = await repo.RevParseAsync("feature");
        var before = await TipsAsync(repo);
        await repo.GitAsync("switch", "-q", "feature");
        await repo.GitAsync("commit", "-q", "--amend", "-m", "Feature, reworded");

        var change = await UpdateAsync(repo, before);

        Assert.Equal(["Feature, reworded"], change.Added.Select(c => c.Subject));
        Assert.Equal([oldFeature], change.Removed);
        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public async Task A_deleted_branch_whose_commits_are_on_another_removes_nothing()
    {
        var repo = await RepoAsync();
        await repo.GitAsync("merge", "-q", "--ff-only", "feature");
        var before = await TipsAsync(repo);
        await repo.GitAsync("branch", "-q", "-d", "feature");

        var change = await UpdateAsync(repo, before);

        // The branch pointed at main's commit, so the tips are the same.
        Assert.Same(HistoryChange.None, change);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public async Task Adding_more_than_the_limit_asks_for_a_full_read_but_exactly_the_limit_is_an_update(int commits, bool tooMany)
    {
        var repo = await RepoAsync();
        var before = await TipsAsync(repo);
        for (var i = 0; i < commits; i++)
            await repo.CommitAsync("file.txt", $"new {i}", $"New {i}");

        var change = await UpdateAsync(repo, before, maxCommits: 3);

        Assert.Equal(tooMany, change.TooMany);
        Assert.Equal(tooMany ? 0 : commits, change.Added.Count);
        Assert.Single(_calls);
    }

    [Fact]
    public async Task Removing_more_than_the_limit_asks_for_a_full_read()
    {
        var repo = await RepoAsync();
        await repo.GitAsync("switch", "-q", "feature");
        for (var i = 0; i < 4; i++)
            await repo.CommitAsync("feature.txt", $"more {i}", $"More {i}");
        await repo.GitAsync("switch", "-q", "main");
        var before = await TipsAsync(repo);
        await repo.GitAsync("branch", "-q", "-D", "feature");

        var change = await UpdateAsync(repo, before, maxCommits: 3);

        Assert.Same(HistoryChange.TooManyChanges, change);
    }

    [Fact]
    public async Task Tips_that_are_not_object_ids_are_refused_without_git()
    {
        var repo = await RepoAsync();
        _calls.Clear();

        await Assert.ThrowsAsync<ArgumentException>(() => Updater().UpdateAsync(repo.Path, ["HEAD"], [], Ct));
        Assert.Empty(_calls);
    }
}
