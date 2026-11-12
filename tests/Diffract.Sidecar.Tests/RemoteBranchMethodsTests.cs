using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// "Delete remote branch" over the real host: planned with <c>cleanup.planRemoteBranch</c>, applied and undone with the
/// ordinary cleanup methods. "mine" follows a bare repo in the temp folder, which stands in for the remote. Nothing
/// here writes outside the temp folder.
/// </summary>
public sealed class RemoteBranchMethodsTests : IAsyncDisposable
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
    /// The remote has main (its default) and "fix/ä-1", which holds a commit that exists nowhere else, as "mine" has no
    /// local branch for it. Opened, scanned.
    /// </summary>
    private async Task OpenAsync()
    {
        _remote = await FixtureRepo.InitAsync(_temp, "remote.git", bare: true);
        _mine = await FixtureRepo.InitAsync(_temp, "mine");
        await _mine.CommitAsync("a.txt", "a\n", "First");
        await _mine.GitAsync("remote", "add", "origin", _remote.Path);
        await _mine.GitAsync("switch", "-q", "-c", "fix/ä-1");
        await _mine.CommitAsync("b.txt", "b\n", "Only on the remote");
        await _mine.GitAsync("push", "-q", "origin", "main", "fix/ä-1");
        await _mine.GitAsync("switch", "-q", "main");
        await _mine.GitAsync("branch", "-q", "-D", "fix/ä-1");
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

    private async Task<JsonElement> PlanAsync(string branch)
    {
        Result(await RequestAsync("repo.refresh", new { repoId = "1" }));
        return await RequestAsync("cleanup.planRemoteBranch", new { repoId = "1", remote = "origin", branch });
    }

    private async Task<string> OnRemoteAsync() => (await _remote.GitAsync("for-each-ref", "--format=%(refname) %(objectname)", "refs/heads")).StandardOutput;

    private async Task<string> RefsAsync() => (await _mine.GitAsync("for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput;

    private static JsonElement Only(JsonElement plan) => plan.GetProperty("leftovers").EnumerateArray().Single();

    [Fact]
    public async Task Plans_the_delete_on_the_remote_with_a_backup_and_says_it_changes_the_remote()
    {
        await OpenAsync();
        var (here, there) = (await RefsAsync(), await OnRemoteAsync());

        var plan = Result(await PlanAsync("fix/ä-1"));

        var steps = Only(plan).GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(["Backup", "DeleteOnRemote"], steps.Select(s => s.GetProperty("kind").GetString()));
        Assert.False(steps[0].TryGetProperty("remote", out var backupRemote) && backupRemote.ValueKind != JsonValueKind.Null);
        Assert.Equal("origin", steps[1].GetProperty("remote").GetString());
        Assert.Equal("Delete branch fix/ä-1 on origin, for everyone", steps[1].GetProperty("description").GetString());
        Assert.Equal(here, await RefsAsync());
        Assert.Equal(there, await OnRemoteAsync());
    }

    [Fact]
    public async Task Applying_deletes_it_on_the_remote_and_undo_pushes_it_back_to_the_same_commit()
    {
        await OpenAsync();
        var (here, there) = (await RefsAsync(), await OnRemoteAsync());
        var plan = Result(await PlanAsync("fix/ä-1"));

        var applied = Result(await RequestAsync("cleanup.apply", new { repoId = "1", planId = plan.GetProperty("planId").GetString() }));

        Assert.All(applied.GetProperty("steps").EnumerateArray(), s => Assert.Equal("Done", s.GetProperty("outcome").GetString()));
        Assert.DoesNotContain("fix/ä-1", await OnRemoteAsync());
        Assert.DoesNotContain("refs/remotes/origin/fix/ä-1", await RefsAsync());

        Result(await RequestAsync("cleanup.undo", new { repoId = "1", cleanupId = applied.GetProperty("cleanupId").GetString() }));

        Assert.Equal(there, await OnRemoteAsync());
        Assert.Contains("refs/remotes/origin/fix/ä-1", await RefsAsync());
        Assert.DoesNotContain("archive/", (await _mine.GitAsync("tag", "--list")).StandardOutput);
        Assert.Equal(here.Split('\n').Length, (await RefsAsync()).Split('\n').Length);
    }

    [Fact]
    public async Task The_default_branch_is_refused_in_the_plan()
    {
        await OpenAsync();

        var plan = Result(await PlanAsync("main"));

        Assert.Equal("main is the default branch of origin. Diffract does not delete it.", Only(plan).GetProperty("refused").GetString());
        Assert.Empty(Only(plan).GetProperty("steps").EnumerateArray());
    }

    [Fact]
    public async Task A_remote_that_cannot_be_reached_plans_nothing_and_says_why()
    {
        await OpenAsync();
        var before = await RefsAsync();
        await _mine.GitAsync("remote", "set-url", "origin", _temp.Combine("gone.git"));

        var response = await PlanAsync("fix/ä-1");

        Assert.Contains("Git could not ask origin which branches it has", response.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(before, await RefsAsync());
    }

    [Fact]
    public async Task Asks_for_the_remote_and_the_branch()
    {
        await OpenAsync();

        var response = await RequestAsync("cleanup.planRemoteBranch", new { repoId = "1", remote = "origin" });

        Assert.Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
    }
}
