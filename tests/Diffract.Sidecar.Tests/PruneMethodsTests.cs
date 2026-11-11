using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// Pruning remote branches over the real host: "mine" follows a bare repo in the temp folder, standing in for the
/// remote. Nothing here writes outside the temp folder.
/// </summary>
public sealed class PruneMethodsTests : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private HostHarness _host = null!;
    private SidecarMethods _methods = null!;
    private FixtureRepo _mine = null!;
    private FixtureRepo _remote = null!;
    private int _nextId = 1;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        _methods?.Dispose();
        _temp.Dispose();
    }

    /// <summary>
    /// Mine has main, keep-me, fix-1 and fix/ä-2 (all pushed, so origin has them too) and a local fix-1. Opened, scanned.
    /// </summary>
    private async Task OpenAsync()
    {
        _remote = await FixtureRepo.InitAsync(_temp, "remote.git", bare: true);
        _mine = await FixtureRepo.InitAsync(_temp, "mine");
        await _mine.CommitAsync("a.txt", "a\n", "First");
        await _mine.GitAsync("remote", "add", "origin", _remote.Path);
        await _mine.GitAsync("branch", "keep-me");
        await _mine.GitAsync("branch", "fix-1");
        await _mine.GitAsync("branch", "fix/ä-2");
        await _mine.GitAsync("push", "-q", "origin", "main", "keep-me", "fix-1", "fix/ä-2");
        await _mine.GitAsync("remote", "set-head", "origin", "main");
        _methods = new SidecarMethods(
            new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) },
            new SidecarDependencies
            {
                Notify = (method, parameters) => _host.Host.NotifyAsync(method, parameters),
                Running = new FakeProcessTable(),
                OperationLogFolder = _temp.Combine("oplog"),
                HiddenRowsFolder = _temp.Combine("hidden"),
            });
        _host = new HostHarness(_methods.Dispatch);
        Result(await RequestAsync("repo.open", new { path = _mine.Path }));
        await _methods.Repos.ScannedAsync("1");
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        var id = _nextId++;
        _host.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        while (true)
        {
            var message = await _host.NextJsonAsync();
            if (message.TryGetProperty("id", out var answered) && answered.GetInt32() == id)
                return message;
        }
    }

    private static JsonElement Result(JsonElement response) =>
        response.TryGetProperty("result", out var result) ? result : throw new InvalidOperationException(response.ToString());

    private async Task<JsonElement> PlanAsync(string remote = "origin")
    {
        Result(await RequestAsync("repo.refresh", new { repoId = "1" }));
        return await RequestAsync("cleanup.planPrune", new { repoId = "1", remote });
    }

    private async Task<string> RefsAsync() => (await _mine.GitAsync("for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput;

    private static IEnumerable<string> Names(JsonElement plan) => plan.GetProperty("leftovers").EnumerateArray().Select(l => l.GetProperty("name").GetString()!);

    [Fact]
    public async Task Lists_exactly_the_branches_the_remote_lost_removes_only_those_and_undo_brings_them_back()
    {
        await OpenAsync();
        await _remote.GitAsync("branch", "-D", "fix-1");
        await _remote.GitAsync("branch", "-D", "fix/ä-2");
        var before = await RefsAsync();

        var plan = Result(await PlanAsync());

        Assert.Equal(["origin/fix-1", "origin/fix/ä-2"], Names(plan).Order(StringComparer.Ordinal));
        var steps = plan.GetProperty("leftovers").EnumerateArray().SelectMany(l => l.GetProperty("steps").EnumerateArray()).Select(s => s.GetProperty("kind").GetString());
        Assert.All(steps, kind => Assert.Equal("DeleteRemoteBranch", kind));
        Assert.Equal(before, await RefsAsync());

        var applied = Result(await RequestAsync("cleanup.apply", new { repoId = "1", planId = plan.GetProperty("planId").GetString() }));

        var after = await RefsAsync();
        Assert.Equal(before.Split('\n').Where(l => !l.Contains("refs/remotes/origin/fix")), after.Split('\n'));
        Assert.Contains("refs/heads/fix-1 ", after);
        Assert.Equal("", (await _mine.GitAsync("tag", "--list")).StandardOutput);
        Assert.Equal(["main", "keep-me"], (await _remote.GitAsync("for-each-ref", "--format=%(refname:short)", "refs/heads")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).OrderByDescending(n => n == "main"));

        Result(await RequestAsync("cleanup.undo", new { repoId = "1", cleanupId = applied.GetProperty("cleanupId").GetString() }));
        Assert.Equal(before, await RefsAsync());
    }

    [Fact]
    public async Task A_repo_where_the_remote_has_everything_plans_nothing()
    {
        await OpenAsync();

        Assert.Empty(Names(Result(await PlanAsync())));
    }

    [Fact]
    public async Task A_remote_that_cannot_be_reached_removes_nothing_and_says_why()
    {
        await OpenAsync();
        var before = await RefsAsync();
        await _mine.GitAsync("remote", "set-url", "origin", _temp.Combine("gone.git"));

        var response = await PlanAsync();

        Assert.Contains("nothing was removed", response.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(before, await RefsAsync());
    }

    [Fact]
    public async Task A_remote_that_reports_no_branches_removes_nothing()
    {
        await OpenAsync();
        var empty = await FixtureRepo.InitAsync(_temp, "empty.git", bare: true);
        await _mine.GitAsync("remote", "set-url", "origin", empty.Path);
        var before = await RefsAsync();

        var response = await PlanAsync();

        Assert.Contains("did not report any branches", response.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(before, await RefsAsync());
    }

    [Fact]
    public async Task The_runner_that_asks_the_remote_cannot_write()
    {
        await OpenAsync();
        var runner = Diffract.Git.Refs.RemoteHeads.RunnerFor(_mine.Path, new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) });

        await Assert.ThrowsAsync<ReadOnlyViolationException>(() => runner.RunAsync(["fetch", "--prune", "origin"], TestContext.Current.CancellationToken));
    }
}
