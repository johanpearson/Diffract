using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;

namespace Diffract.Sidecar.Tests;

/// <summary>Saving, applying and dropping stashes over the real host, in a fixture repo with å ä ö in its paths.</summary>
public sealed class StashMethodsTests : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private HostHarness _host = null!;
    private SidecarMethods _methods = null!;
    private FixtureRepo _repo = null!;
    private int _nextId = 1;

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _methods.Dispose();
        _temp.Dispose();
    }

    /// <summary>A repo with one commit, opened, with the scan after opening done.</summary>
    private async Task OpenAsync()
    {
        _repo = await FixtureRepo.InitAsync(_temp, "repo åäö");
        await _repo.CommitAsync("fil.txt", "one\n", "Initial commit");
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

    /// <summary>Sends a request and returns its response, skipping the notifications that arrive first.</summary>
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

    private static JsonElement Error(JsonElement response) => response.GetProperty("error");

    private async Task<string> StashListAsync() => (await _repo.GitAsync("stash", "list", "--format=%gs")).StandardOutput.Trim();

    private string Read(string file) => File.ReadAllText(Path.Combine(_repo.Path, file));

    private async Task<string> PushAsync(string message, bool includeUntracked) =>
        Result(await RequestAsync("stash.push", new { repoId = "1", worktree = _repo.Path, message, includeUntracked })).GetProperty("commitId").GetString()!;

    /// <summary>The snapshot the drop is planned from is the one a refresh reads, as the watcher would.</summary>
    private async Task<JsonElement> PlanDropAsync(string commit)
    {
        Result(await RequestAsync("repo.refresh", new { repoId = "1" }));
        return await RequestAsync("cleanup.planStash", new { repoId = "1", commit });
    }

    [Fact]
    public async Task Save_with_new_files_apply_after_the_list_moved_then_drop_and_undo()
    {
        await OpenAsync();
        _repo.WriteFile("fil.txt", "mine\n");
        _repo.WriteFile("ny fil.txt", "new\n");

        var mine = await PushAsync("Halvfärdigt", includeUntracked: true);
        Assert.False(File.Exists(Path.Combine(_repo.Path, "ny fil.txt")));
        _repo.WriteFile("fil.txt", "an agent's\n");
        await PushAsync("An agent's", includeUntracked: false);

        var applied = Result(await RequestAsync("stash.apply", new { repoId = "1", worktree = _repo.Path, commit = mine }));
        Assert.False(applied.GetProperty("conflicts").GetBoolean());
        Assert.Equal(2, applied.GetProperty("status").GetProperty("files").GetArrayLength());
        Assert.Equal(("mine\n", "new\n"), (Read("fil.txt"), Read("ny fil.txt")));

        var plan = Result(await PlanDropAsync(mine));
        var steps = plan.GetProperty("leftovers")[0].GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("kind").GetString());
        Assert.Equal(["Backup", "DropStash"], steps);
        var dropped = Result(await RequestAsync("cleanup.apply", new { repoId = "1", planId = plan.GetProperty("planId").GetString() }));
        Assert.Equal("On main: An agent's", await StashListAsync());

        Result(await RequestAsync("cleanup.undo", new { repoId = "1", cleanupId = dropped.GetProperty("cleanupId").GetString() }));
        Assert.Equal("On main: Halvfärdigt\nOn main: An agent's", await StashListAsync());
        Assert.Equal(mine, await _repo.RevParseAsync("stash@{0}"));
    }

    [Fact]
    public async Task An_apply_with_conflicts_keeps_the_stash_and_lists_the_conflicted_file()
    {
        await OpenAsync();
        _repo.WriteFile("fil.txt", "stashed\n");
        var stash = await PushAsync("Mine", includeUntracked: false);
        await _repo.CommitAsync("fil.txt", "committed since\n", "Change it");

        var applied = Result(await RequestAsync("stash.apply", new { repoId = "1", worktree = _repo.Path, commit = stash }));

        Assert.True(applied.GetProperty("conflicts").GetBoolean());
        Assert.Equal("Conflicted", Assert.Single(applied.GetProperty("status").GetProperty("files").EnumerateArray()).GetProperty("kind").GetString());
        Assert.Equal("On main: Mine", await StashListAsync());
    }

    [Fact]
    public async Task The_refs_carry_each_stashs_message_and_no_other_ref_has_one()
    {
        await OpenAsync();
        _repo.WriteFile("fil.txt", "two\n");
        var stash = await PushAsync("Halvfärdigt", includeUntracked: false);
        Result(await RequestAsync("repo.refresh", new { repoId = "1" }));

        var refs = Result(await RequestAsync("history.refs", new { repoId = "1" })).GetProperty("refs").EnumerateArray()
            .Select(r => (r.GetProperty("name").GetString(), r.GetProperty("targetId").GetString(), r.GetProperty("message").GetString())).ToList();

        Assert.Equal([("main", await _repo.RevParseAsync("main"), null), ("stash@{0}", stash, "On main: Halvfärdigt")], refs);
    }

    [Fact]
    public async Task Nothing_to_stash_is_an_error_that_says_so()
    {
        await OpenAsync();

        var error = Error(await RequestAsync("stash.push", new { repoId = "1", worktree = _repo.Path, includeUntracked = true }));

        Assert.Contains("nothing to stash", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Apply_takes_only_a_full_commit_id()
    {
        await OpenAsync();

        var error = Error(await RequestAsync("stash.apply", new { repoId = "1", worktree = _repo.Path, commit = "stash@{0}" }));

        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task A_drop_is_not_planned_for_a_stash_that_is_no_longer_in_the_list()
    {
        await OpenAsync();
        _repo.WriteFile("fil.txt", "two\n");
        var stash = await PushAsync("Mine", includeUntracked: false);
        await _repo.GitAsync("stash", "drop");

        var error = Error(await PlanDropAsync(stash));

        Assert.Contains("no longer in the list", error.GetProperty("message").GetString());
    }
}
