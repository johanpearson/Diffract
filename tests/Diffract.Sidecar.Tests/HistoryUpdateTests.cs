using System.Collections.Concurrent;
using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// history.update, history.refs and the toggles, against fixture repos that change after the history was read: a
/// branch gets a commit, a branch is added, a branch is force-moved. The watcher may refresh at any time, so the call
/// budget counts only history's own git calls, the ones that read revisions from standard input.
/// </summary>
public sealed class HistoryUpdateTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConcurrentQueue<GitCall> _calls = new();
    private SidecarMethods _methods = null!;

    public void Dispose()
    {
        _methods.Dispose();
        _temp.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private int HistoryCalls => _calls.Count(c => c.Arguments.Contains("--stdin"));

    /// <summary>main with two commits, feature with one more, a Copilot checkpoint on its own commit and a stash.</summary>
    private async Task<FixtureRepo> OpenAsync()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        await repo.CommitAsync("file.txt", "1", "First");
        await repo.CommitAsync("file.txt", "2", "Second");
        await repo.GitAsync("switch", "-q", "-c", "feature");
        await repo.CommitAsync("feature.txt", "1", "Feature");
        await repo.CommitAsync("feature.txt", "2", "copilot checkpoint: saved");
        await repo.GitAsync("update-ref", "refs/copilot/checkpoints/session/1", "HEAD");
        await repo.GitAsync("update-ref", "refs/notes/commits", "HEAD");
        await repo.GitAsync("reset", "-q", "--hard", "HEAD~1");
        await repo.GitAsync("switch", "-q", "main");
        repo.WriteFile("file.txt", "stashed");
        await repo.GitAsync("stash", "push", "-q", "-m", "Work in progress");

        _methods = new SidecarMethods(new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp), OnCall = _calls.Enqueue });
        await CallAsync("repo.open", new { path = repo.Path });
        await _methods.Repos.ScannedAsync("1");
        return repo;
    }

    private async Task<JsonElement> CallAsync(string method, object parameters) =>
        await _methods.Dispatch(method, JsonSerializer.SerializeToElement(parameters), Ct);

    /// <summary>Reads the history and answers with its tips.</summary>
    private async Task<string[]> ReadTipsAsync(bool checkpoints = false, bool stashes = false) =>
        Strings((await CallAsync("history.read", new { repoId = "1", token = "t", checkpoints, stashes })).GetProperty("tips"));

    /// <summary>Takes a fresh snapshot, as the watcher would, then asks what changed since <paramref name="tips"/>.</summary>
    private async Task<JsonElement> UpdateAsync(string[] tips, bool checkpoints = false, bool stashes = false)
    {
        await CallAsync("repo.refresh", new { repoId = "1", includeUnreachable = false });
        _calls.Clear();
        return await CallAsync("history.update", new { repoId = "1", tips, checkpoints, stashes });
    }

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    private static string[] Subjects(JsonElement update) => [.. update.GetProperty("added").EnumerateArray().Select(c => c.GetProperty("subject").GetString()!)];

    [Fact]
    public async Task Nothing_changed_needs_no_git()
    {
        await OpenAsync();
        var tips = await ReadTipsAsync();

        var update = await UpdateAsync(tips);

        Assert.Empty(Subjects(update));
        Assert.Empty(Strings(update.GetProperty("removed")));
        Assert.Equal(tips.Order(), Strings(update.GetProperty("tips")).Order());
        Assert.False(update.GetProperty("reread").GetBoolean());
        Assert.Equal(0, HistoryCalls);
    }

    [Fact]
    public async Task A_branch_that_gets_a_commit_adds_it_with_one_git_call()
    {
        var repo = await OpenAsync();
        var tips = await ReadTipsAsync();
        await repo.CommitAsync("file.txt", "3", "Third");

        var update = await UpdateAsync(tips);

        Assert.Equal(["Third"], Subjects(update));
        Assert.Empty(Strings(update.GetProperty("removed")));
        Assert.Contains(await repo.RevParseAsync("main"), Strings(update.GetProperty("tips")));
        Assert.Equal(1, HistoryCalls);
    }

    [Fact]
    public async Task An_added_branch_adds_its_own_commits_with_one_git_call()
    {
        var repo = await OpenAsync();
        var tips = await ReadTipsAsync();
        await repo.GitAsync("switch", "-q", "-c", "claude/fix", "HEAD~1");
        await repo.CommitAsync("fix.txt", "1", "Fix");

        var update = await UpdateAsync(tips);

        Assert.Equal(["Fix"], Subjects(update));
        Assert.Equal(1, HistoryCalls);
    }

    [Fact]
    public async Task A_force_moved_branch_removes_what_it_left_behind_with_two_git_calls()
    {
        var repo = await OpenAsync();
        var feature = await repo.RevParseAsync("feature");
        var tips = await ReadTipsAsync();
        await repo.GitAsync("switch", "-q", "feature");
        await repo.GitAsync("commit", "-q", "--amend", "-m", "Feature, reworded");

        var update = await UpdateAsync(tips);

        Assert.Equal(["Feature, reworded"], Subjects(update));
        Assert.Equal([feature], Strings(update.GetProperty("removed")));
        Assert.Equal(2, HistoryCalls);
    }

    [Fact]
    public async Task Checkpoints_and_stashes_are_left_out_until_asked_for()
    {
        await OpenAsync();
        var tips = await ReadTipsAsync();

        var withCheckpoints = await UpdateAsync(tips, checkpoints: true);
        var withStashes = await UpdateAsync(tips, stashes: true);

        Assert.Equal(["copilot checkpoint: saved"], Subjects(withCheckpoints));
        // A stash is a commit with the index as its second parent, and git shows both.
        var stash = Subjects(withStashes);
        Assert.Equal(2, stash.Length);
        Assert.Equal("On main: Work in progress", stash[0]);
        Assert.StartsWith("index on main: ", stash[1]);
    }

    [Fact]
    public async Task Turning_checkpoints_off_again_removes_their_commits()
    {
        await OpenAsync();
        var tips = await ReadTipsAsync(checkpoints: true);

        var update = await UpdateAsync(tips, checkpoints: false);

        Assert.Empty(Subjects(update));
        Assert.Single(Strings(update.GetProperty("removed")));
    }

    [Fact]
    public async Task Refs_include_checkpoints_and_stashes_with_agent_hints_but_not_other_refs()
    {
        var repo = await OpenAsync();
        await repo.GitAsync("worktree", "add", "-q", "-b", "claude/task", _temp.Combine(".claude", "worktrees", "task"), "main");
        await CallAsync("repo.refresh", new { repoId = "1", includeUnreachable = false });

        var result = await CallAsync("history.refs", new { repoId = "1" });

        var refs = result.GetProperty("refs").EnumerateArray()
            .Select(r => (r.GetProperty("name").GetString(), r.GetProperty("kind").GetString(), r.GetProperty("agent").GetString()))
            .Order().ToList();
        Assert.Equal(
            [
                ("claude/task", "Branch", "Claude Code"),
                ("copilot/checkpoints/session/1", "Checkpoint", "GitHub Copilot"),
                ("feature", "Branch", null),
                ("main", "Branch", null),
                ("stash@{0}", "Stash", null),
            ],
            refs);
        Assert.Equal([await repo.RevParseAsync("main")], Strings(result.GetProperty("agentHeads")));
        Assert.Contains("copilot checkpoint: ", Strings(result.GetProperty("agentSubjectPrefixes")));
    }

    [Theory]
    [InlineData("""{"repoId":"1"}""")]
    [InlineData("""{"repoId":"1","tips":["HEAD"]}""")]
    [InlineData("""{"repoId":"1","tips":["--all"]}""")]
    public async Task Tips_that_are_not_commit_ids_are_refused_without_git(string parameters)
    {
        await OpenAsync();
        _calls.Clear();

        var error = await Assert.ThrowsAsync<Rpc.RpcException>(() => _methods.Dispatch("history.update", JsonDocument.Parse(parameters).RootElement, Ct));

        Assert.Equal("'tips' must be a list of full commit ids", error.Message);
        Assert.Empty(_calls);
    }
}
