using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// "Delete branch" from the history menu over the real host: planned with <c>cleanup.planBranch</c>, applied and undone
/// with the ordinary cleanup methods. Every repo is a fixture in the temp folder.
/// </summary>
public sealed class BranchMethodsTests : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private HostHarness _host = null!;
    private SidecarMethods _methods = null!;
    private FixtureRepo _repo = null!;
    private int _nextId = 1;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        _methods?.Dispose();
        _temp.Dispose();
    }

    /// <summary>
    /// main with two commits. "merged" is on main. "wip/ä-1" has a commit nowhere else. "busy" is checked out in a
    /// second worktree. Opened, scanned, with main checked out.
    /// </summary>
    private async Task OpenAsync()
    {
        _repo = await FixtureRepo.InitAsync(_temp, "mine");
        await _repo.CommitAsync("a.txt", "a\n", "First");
        await _repo.CommitAsync("a.txt", "b\n", "Second");
        await _repo.GitAsync("branch", "merged");
        await _repo.GitAsync("switch", "-q", "-c", "wip/ä-1");
        await _repo.CommitAsync("wip.txt", "work\n", "Work in progress");
        await _repo.GitAsync("switch", "-q", "main");
        await _repo.GitAsync("worktree", "add", "-q", "-b", "busy", _temp.Combine("busy-tree"));
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
        Result(await RequestAsync("repo.open", new { path = _repo.Path }));
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
        return Result(await RequestAsync("cleanup.planBranch", new { repoId = "1", branch }));
    }

    private async Task<string> RefsAsync() => (await _repo.GitAsync("for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput;

    private static JsonElement OnlyBranch(JsonElement plan) => plan.GetProperty("leftovers").EnumerateArray().Single();

    private static string[] StepKinds(JsonElement leftover) => [.. leftover.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("kind").GetString()!)];

    [Fact]
    public async Task A_merged_branch_is_deleted_without_a_backup_tag_and_undo_brings_it_back()
    {
        await OpenAsync();
        var before = await RefsAsync();

        var plan = await PlanAsync("merged");

        Assert.Equal("LocalBranch", OnlyBranch(plan).GetProperty("kind").GetString());
        Assert.Equal(["DeleteBranch"], StepKinds(OnlyBranch(plan)));
        Assert.Equal(before, await RefsAsync());

        var applied = Result(await RequestAsync("cleanup.apply", new { repoId = "1", planId = plan.GetProperty("planId").GetString() }));

        Assert.DoesNotContain("refs/heads/merged ", await RefsAsync());
        Assert.Equal("", (await _repo.GitAsync("tag", "--list")).StandardOutput);

        Result(await RequestAsync("cleanup.undo", new { repoId = "1", cleanupId = applied.GetProperty("cleanupId").GetString() }));
        Assert.Equal(before, await RefsAsync());
    }

    [Fact]
    public async Task A_branch_with_work_nowhere_else_gets_a_backup_tag_first_and_undo_brings_it_back()
    {
        await OpenAsync();
        var before = await RefsAsync();

        var plan = await PlanAsync("wip/ä-1");

        Assert.Equal(["Backup", "DeleteBranch"], StepKinds(OnlyBranch(plan)));

        var applied = Result(await RequestAsync("cleanup.apply", new { repoId = "1", planId = plan.GetProperty("planId").GetString() }));

        var after = await RefsAsync();
        Assert.DoesNotContain("refs/heads/wip/ä-1 ", after);
        Assert.Contains("refs/tags/archive/", after);

        Result(await RequestAsync("cleanup.undo", new { repoId = "1", cleanupId = applied.GetProperty("cleanupId").GetString() }));
        Assert.Equal(before, await RefsAsync());
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("main")]
    public async Task A_branch_checked_out_in_a_worktree_is_refused_and_nothing_is_removed(string branch)
    {
        await OpenAsync();
        var before = await RefsAsync();

        var plan = await PlanAsync(branch);

        var leftover = OnlyBranch(plan);
        Assert.Equal([], StepKinds(leftover));
        Assert.Contains("In use", leftover.GetProperty("refused").GetString());
        await RequestAsync("cleanup.apply", new { repoId = "1", planId = plan.GetProperty("planId").GetString() });
        Assert.Equal(before, await RefsAsync());
    }

    [Fact]
    public async Task A_branch_that_is_not_there_is_refused()
    {
        await OpenAsync();

        var leftover = OnlyBranch(await PlanAsync("nope"));

        Assert.Equal([], StepKinds(leftover));
        Assert.Contains("no longer there", leftover.GetProperty("refused").GetString());
    }

    [Fact]
    public async Task A_remote_tracking_branch_is_not_a_local_branch()
    {
        await OpenAsync();
        var remote = await FixtureRepo.InitAsync(_temp, "remote.git", bare: true);
        await _repo.GitAsync("remote", "add", "origin", remote.Path);
        await _repo.GitAsync("push", "-q", "origin", "merged");

        var leftover = OnlyBranch(await PlanAsync("origin/merged"));

        Assert.Equal([], StepKinds(leftover));
    }

    [Fact]
    public async Task A_missing_branch_name_is_an_invalid_request()
    {
        await OpenAsync();

        var response = await RequestAsync("cleanup.planBranch", new { repoId = "1" });

        Assert.Contains("branch", response.GetProperty("error").GetProperty("message").GetString());
    }
}
