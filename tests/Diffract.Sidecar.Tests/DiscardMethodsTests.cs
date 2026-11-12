using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Leftovers.Cleanup;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// Discarding over the real host: plan, apply and undo through <c>cleanup.planDiscard</c>, <c>cleanup.apply</c> and
/// <c>cleanup.undo</c>. Nothing here writes outside the temp folder.
/// </summary>
public sealed class DiscardMethodsTests : IAsyncDisposable
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

    private async Task OpenAsync()
    {
        _repo = await FixtureRepo.InitAsync(_temp, "repo");
        await _repo.CommitAsync("a.txt", "one\ntwo\n", "First");
        _methods = new SidecarMethods(
            new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) },
            new SidecarDependencies
            {
                Notify = (method, parameters) => _host.Host.NotifyAsync(method, parameters),
                Running = new FakeProcessTable(),
                OperationLogFolder = _temp.Combine("oplog"),
                HiddenRowsFolder = _temp.Combine("hidden"),
                DiscardBackups = new DiscardBackups(_temp.Combine("copies")),
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

    private static string ErrorOf(JsonElement response) => response.GetProperty("error").GetProperty("message").GetString()!;

    private Task<JsonElement> PlanFilesAsync(params string[] paths) =>
        RequestAsync("cleanup.planDiscard", new { repoId = "1", worktree = _repo.Path, paths });

    private string Read(string path) => File.ReadAllText(Path.Combine(_repo.Path, path));

    [Fact]
    public async Task A_file_is_planned_applied_and_undone_and_the_dialog_names_what_will_be_thrown_away()
    {
        await OpenAsync();
        _repo.WriteFile("a.txt", "one\nTWO\n");
        _repo.WriteFile("new åäö.txt", "ny\n");

        var plan = Result(await PlanFilesAsync("a.txt", "new åäö.txt"));

        var leftovers = plan.GetProperty("leftovers").EnumerateArray().ToList();
        Assert.All(leftovers, l => Assert.Equal("DiscardedChange", l.GetProperty("kind").GetString()));
        Assert.Equal(
            ["Discard all unstaged changes in a.txt", "Delete the new file new åäö.txt"],
            leftovers.Select(l => l.GetProperty("steps")[0].GetProperty("description").GetString()));
        Assert.All(leftovers, l => Assert.True(l.GetProperty("steps")[0].GetProperty("undoable").GetBoolean()));
        Assert.Equal("one\nTWO\n", Read("a.txt"));

        var applied = Result(await RequestAsync("cleanup.apply", new { repoId = "1", planId = plan.GetProperty("planId").GetString() }));

        Assert.All(applied.GetProperty("steps").EnumerateArray(), s => Assert.Equal("Done", s.GetProperty("outcome").GetString()));
        Assert.Equal("one\ntwo\n", Read("a.txt"));
        Assert.False(File.Exists(Path.Combine(_repo.Path, "new åäö.txt")));

        Result(await RequestAsync("cleanup.undo", new { repoId = "1", cleanupId = applied.GetProperty("cleanupId").GetString() }));

        Assert.Equal("one\nTWO\n", Read("a.txt"));
        Assert.Equal("ny\n", Read("new åäö.txt"));
    }

    [Fact]
    public async Task A_plan_changes_nothing_until_it_is_applied_and_is_applied_once()
    {
        await OpenAsync();
        _repo.WriteFile("a.txt", "one\nTWO\n");
        var plan = Result(await PlanFilesAsync("a.txt"));
        var planId = plan.GetProperty("planId").GetString();
        Assert.Equal("one\nTWO\n", Read("a.txt"));

        Result(await RequestAsync("cleanup.apply", new { repoId = "1", planId }));
        var again = await RequestAsync("cleanup.apply", new { repoId = "1", planId });

        Assert.Contains("applied already", ErrorOf(again));
    }

    [Fact]
    public async Task One_hunk_is_planned_with_where_it_is_and_reversed_while_the_other_stays()
    {
        await OpenAsync();
        var lines = string.Concat(Enumerable.Range(1, 20).Select(i => $"line {i}\n"));
        await _repo.CommitAsync("f.txt", lines, "Twenty lines");
        _repo.WriteFile("f.txt", lines.Replace("line 3\n", "THREE\n").Replace("line 15\n", "FIFTEEN\n"));
        var status = Result(await RequestAsync("changes.status", new { repoId = "1", worktree = _repo.Path }));
        var changed = status.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("path").GetString() == "f.txt");
        var file = Result(await RequestAsync("changes.file", new
        {
            repoId = "1", worktree = _repo.Path, path = "f.txt", staged = false,
            headId = changed.GetProperty("headId").GetString(), indexId = changed.GetProperty("indexId").GetString(),
        }));
        var hunks = file.GetProperty("hunks").EnumerateArray().Select(h => h.GetProperty("id").GetString()).ToList();

        var plan = Result(await RequestAsync("cleanup.planDiscard", new { repoId = "1", worktree = _repo.Path, hunk = new { path = "f.txt", id = hunks[1] } }));

        Assert.Equal(
            "Discard the change at line 15 (replaces your 1 line with 1 line) in f.txt",
            plan.GetProperty("leftovers")[0].GetProperty("steps")[0].GetProperty("description").GetString());
        Result(await RequestAsync("cleanup.apply", new { repoId = "1", planId = plan.GetProperty("planId").GetString() }));
        Assert.Equal(lines.Replace("line 3\n", "THREE\n"), Read("f.txt"));
    }

    [Fact]
    public async Task A_file_with_nothing_unstaged_is_refused_before_any_dialog()
    {
        await OpenAsync();

        var response = await PlanFilesAsync("a.txt");

        Assert.Contains("has no unstaged changes to discard", ErrorOf(response));
    }

    [Fact]
    public async Task A_hunk_that_changed_since_it_was_shown_is_refused()
    {
        await OpenAsync();

        var response = await RequestAsync("cleanup.planDiscard", new { repoId = "1", worktree = _repo.Path, hunk = new { path = "a.txt", id = "00000000000000000000000000000000" } });

        Assert.True(response.TryGetProperty("error", out _), response.ToString());
        Assert.Equal("one\ntwo\n", Read("a.txt"));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(".git/config")]
    public async Task A_path_outside_the_worktree_is_refused(string path)
    {
        await OpenAsync();

        var response = await PlanFilesAsync(path);

        Assert.True(response.TryGetProperty("error", out _), response.ToString());
    }

    [Fact]
    public async Task A_worktree_the_repo_does_not_have_is_refused()
    {
        await OpenAsync();

        var response = await RequestAsync("cleanup.planDiscard", new { repoId = "1", worktree = _temp.Combine("elsewhere"), paths = new[] { "a.txt" } });

        Assert.Contains("Not a worktree of this repo", ErrorOf(response));
    }

    [Fact]
    public async Task Paths_and_a_hunk_together_or_neither_are_refused()
    {
        await OpenAsync();

        var both = await RequestAsync("cleanup.planDiscard", new { repoId = "1", worktree = _repo.Path, paths = new[] { "a.txt" }, hunk = new { path = "a.txt", id = "x" } });
        var neither = await RequestAsync("cleanup.planDiscard", new { repoId = "1", worktree = _repo.Path });

        Assert.Contains("either 'paths' or 'hunk'", ErrorOf(both));
        Assert.Contains("either 'paths' or 'hunk'", ErrorOf(neither));
    }

    [Fact]
    public async Task A_discard_is_in_the_history_and_can_be_undone_from_there()
    {
        await OpenAsync();
        _repo.WriteFile("a.txt", "one\nTWO\n");
        var plan = Result(await PlanFilesAsync("a.txt"));
        Result(await RequestAsync("cleanup.apply", new { repoId = "1", planId = plan.GetProperty("planId").GetString() }));

        var history = Result(await RequestAsync("cleanup.history", new { repoId = "1" }));

        var past = history[0];
        Assert.True(past.GetProperty("canUndo").GetBoolean());
        Assert.Equal("DiscardedChange", past.GetProperty("leftovers")[0].GetProperty("kind").GetString());
    }
}
