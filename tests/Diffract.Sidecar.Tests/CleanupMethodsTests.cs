using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Leftovers.Cleanup;

namespace Diffract.Sidecar.Tests;

/// <summary>Plan, apply, history and undo over the real host, against the milestone 1 fixture scenarios.</summary>
public sealed class CleanupMethodsTests : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<JsonElement> _notifications = [];
    private HostHarness _host = null!;
    private SidecarMethods _methods = null!;
    private int _nextId = 1;

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _methods.Dispose();
        _temp.Dispose();
    }

    /// <summary>Builds the scenario, opens it and waits for the scan, which lists the leftovers.</summary>
    private async Task<FixtureScenario> OpenAsync(string scenario)
    {
        var built = await new FixtureRepoBuilder(_temp).BuildAsync(scenario);
        _methods = new SidecarMethods(
            new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) },
            new SidecarDependencies
            {
                Notify = (method, parameters) => _host.Host.NotifyAsync(method, parameters),
                Running = new FakeProcessTable(),
                OperationLogFolder = _temp.Combine("oplog"),
            });
        _host = new HostHarness(_methods.Dispatch);
        Result(await RequestAsync("repo.open", new { path = built.RepoPath }));
        await _methods.Repos.ScannedAsync("1");
        return built;
    }

    /// <summary>Sends a request and returns its response. The notifications that arrive first are kept, in order.</summary>
    private async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        var id = _nextId++;
        _host.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        while (true)
        {
            var message = await _host.NextJsonAsync();
            if (message.TryGetProperty("id", out var answered) && answered.GetInt32() == id)
                return message;
            _notifications.Add(message);
        }
    }

    private static JsonElement Result(JsonElement response) =>
        response.TryGetProperty("result", out var result) ? result : throw new InvalidOperationException(response.ToString());

    private static JsonElement Error(JsonElement response) => response.GetProperty("error");

    private static object Selected(string kind, string name, string? path = null) => new { kind, name, path };

    private async Task<JsonElement> PlanAsync(params object[] leftovers) =>
        Result(await RequestAsync("cleanup.plan", new { repoId = "1", leftovers }));

    private static List<string> Outcomes(JsonElement result) =>
        [.. result.GetProperty("steps").EnumerateArray().Select(s => $"{s.GetProperty("kind").GetString()} {s.GetProperty("outcome").GetString()}")];

    [Fact]
    public async Task A_branch_is_planned_applied_and_undone_and_the_ui_hears_of_each_change_before_the_answer()
    {
        await OpenAsync("AgentBranchUnmerged");

        var plan = await PlanAsync(Selected("LocalBranch", "copilot/add-logging"));
        var steps = plan.GetProperty("leftovers")[0].GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(["Backup", "DeleteBranch"], steps.Select(s => s.GetProperty("kind").GetString()));
        Assert.All(steps, s => Assert.True(s.GetProperty("undoable").GetBoolean()));

        _notifications.Clear();
        var applied = Result(await RequestAsync("cleanup.apply", new { repoId = "1", planId = plan.GetProperty("planId").GetString() }));

        Assert.Equal(["Backup Done", "DeleteBranch Done"], Outcomes(applied));
        var changed = Assert.Single(_notifications, n => n.GetProperty("method").GetString() == "repo.changed").GetProperty("params");
        Assert.Equal("Everything", changed.GetProperty("changes")[0].GetProperty("kind").GetString());
        // The fixture dates every commit and tag in January 2026, so the new backup tag is listed as an old one here.
        Assert.DoesNotContain(changed.GetProperty("state").GetProperty("leftovers").EnumerateArray(), l => l.GetProperty("kind").GetString() == "LocalBranch");

        var cleanupId = applied.GetProperty("cleanupId").GetString();
        var waiting = Result(await RequestAsync("cleanup.history", new { repoId = "1" }));
        Assert.True(Assert.Single(waiting.EnumerateArray()).GetProperty("canUndo").GetBoolean());

        var undone = Result(await RequestAsync("cleanup.undo", new { repoId = "1", cleanupId }));
        var history = Result(await RequestAsync("cleanup.history", new { repoId = "1" }));

        Assert.Equal(["DeleteBranch Done", "Backup Done"], Outcomes(undone));
        Assert.Equal([undone.GetProperty("cleanupId").GetString(), cleanupId], history.EnumerateArray().Select(c => c.GetProperty("cleanupId").GetString()));
        Assert.False(history[1].GetProperty("canUndo").GetBoolean());
    }

    [Fact]
    public async Task A_plan_is_applied_once()
    {
        await OpenAsync("AgentBranchMerged");
        var planId = (await PlanAsync(Selected("LocalBranch", "copilot/update-deps"))).GetProperty("planId").GetString();
        Result(await RequestAsync("cleanup.apply", new { repoId = "1", planId }));

        var again = Error(await RequestAsync("cleanup.apply", new { repoId = "1", planId }));

        Assert.Equal("CleanupRefused", again.GetProperty("data").GetProperty("kind").GetString());
        Assert.Equal("This plan is no longer there, or was applied already. Select the leftovers again.", again.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_selected_leftover_the_repo_no_longer_has_and_one_in_use_are_refused_with_why()
    {
        await OpenAsync("DetachedWorktree");
        var dirty = _methods.Repos.LeftoversOf(_methods.Repos.Get("1")).Leftovers.Single(l => l.Name == "detached-dirty");

        var plan = await PlanAsync(Selected("LocalBranch", "gone"), Selected("Worktree", dirty.Name, dirty.Path));

        Assert.Equal(
            ["It is no longer a leftover. Refresh to see the repo as it is now.", "In use, so it is never cleaned up."],
            plan.GetProperty("leftovers").EnumerateArray().Select(l => l.GetProperty("refused").GetString()));
    }

    [Fact]
    public async Task A_worktrees_branch_can_be_left_out_but_no_step_without_a_checkbox()
    {
        await OpenAsync("MergedWorktree");
        var leftover = Assert.Single(_methods.Repos.LeftoversOf(_methods.Repos.Get("1")).Leftovers);
        var plan = await PlanAsync(Selected("Worktree", leftover.Name, leftover.Path));
        var planId = plan.GetProperty("planId").GetString();
        Assert.Equal([false, true], plan.GetProperty("leftovers")[0].GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("optional").GetBoolean()));

        var wrong = Error(await RequestAsync("cleanup.apply", new { repoId = "1", planId, leaveOut = new[] { new { leftover = 0, step = 0 } } }));
        var replanned = (await PlanAsync(Selected("Worktree", leftover.Name, leftover.Path))).GetProperty("planId").GetString();
        var applied = Result(await RequestAsync("cleanup.apply", new { repoId = "1", planId = replanned, leaveOut = new[] { new { leftover = 0, step = 1 } } }));

        Assert.Equal(-32602, wrong.GetProperty("code").GetInt32());
        Assert.Equal(["RemoveWorktree Done", "DeleteBranch Skipped"], Outcomes(applied));
    }

    [Fact]
    public async Task An_operation_gets_each_cleanup_command_for_the_command_window()
    {
        await OpenAsync("AgentBranchUnmerged");
        var planId = (await PlanAsync(Selected("LocalBranch", "copilot/add-logging"))).GetProperty("planId").GetString();
        _notifications.Clear();

        Result(await RequestAsync("cleanup.apply", new { repoId = "1", planId, operation = "op-1" }));

        var commands = _notifications
            .Where(n => n.GetProperty("method").GetString() == "command.output")
            .Select(n => n.GetProperty("params"))
            .Where(p => p.GetProperty("kind").GetString() == "Command")
            .Select(p => p.GetProperty("text").GetString()!)
            .ToList();
        Assert.Equal(2, commands.Count);
        Assert.StartsWith("git tag -a archive/branch/copilot/add-logging-", commands[0]);
        Assert.Equal("git branch -D copilot/add-logging", commands[1]);
    }

    [Fact]
    public async Task Keeping_orphaned_commits_counts_them_again_so_their_rows_go_and_undo_brings_them_back()
    {
        await OpenAsync("OrphanedCommit");
        var plan = await PlanAsync(Selected("OrphanedCommit", "1 commit"), Selected("OrphanedCommit", "2 commits"));
        _notifications.Clear();

        var kept = Result(await RequestAsync("cleanup.apply", new { repoId = "1", planId = plan.GetProperty("planId").GetString() }));

        Assert.Equal(["Backup Done", "KeepCommits Done"], Outcomes(kept));
        // The fixture dates the new tags in January 2026, so here they are listed as old backup tags.
        Assert.DoesNotContain(LastState().GetProperty("leftovers").EnumerateArray(), l => l.GetProperty("kind").GetString() == "OrphanedCommit");

        Result(await RequestAsync("cleanup.undo", new { repoId = "1", cleanupId = kept.GetProperty("cleanupId").GetString() }));

        Assert.Equal(["1 commit", "2 commits"], LastState().GetProperty("leftovers").EnumerateArray().Select(l => l.GetProperty("name").GetString()).Order());
    }

    /// <summary>The state in the last repo.changed.</summary>
    private JsonElement LastState() =>
        _notifications.Last(n => n.GetProperty("method").GetString() == "repo.changed").GetProperty("params").GetProperty("state");

    [Fact]
    public async Task One_cleanup_runs_at_a_time_per_repo()
    {
        await OpenAsync("Clean");
        var hold = new TaskCompletionSource<int>();
        var first = _methods.Repos.CleanUpAsync(_methods.Repos.Get("1"), AfterCleanup.Refresh, _ => hold.Task);

        var second = await Assert.ThrowsAsync<CleanupLogException>(() => _methods.Repos.CleanUpAsync(_methods.Repos.Get("1"), AfterCleanup.Refresh, _ => Task.FromResult(0)));
        hold.SetResult(1);

        Assert.Equal("A cleanup is already running in this repo. Wait for it to finish.", second.Message);
        Assert.Equal(1, await first);
        Assert.Equal(0, await _methods.Repos.CleanUpAsync(_methods.Repos.Get("1"), AfterCleanup.Refresh, _ => Task.FromResult(0)));
    }
}
