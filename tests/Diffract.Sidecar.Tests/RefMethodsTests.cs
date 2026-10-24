using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// The history menu's methods over the real host, against a fixture repo: main with two commits, a side branch on
/// the first, and a linked worktree on a branch of its own. Every change to it is made before it is opened.
/// </summary>
public sealed class RefMethodsTests : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private HostHarness _host = null!;
    private SidecarMethods _methods = null!;
    private int _nextId = 1;

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _methods.Dispose();
        _temp.Dispose();
    }

    private sealed record Repo(FixtureRepo Git, string Linked, string First, string Second);

    private async Task<Repo> OpenAsync(Func<Repo, Task>? prepare = null)
    {
        var git = await FixtureRepo.InitAsync(_temp, "repo");
        var first = await git.CommitAsync("a.txt", "a\n", "First");
        var second = await git.CommitAsync("a.txt", "aa\n", "Second");
        await git.GitAsync("branch", "side", first);
        var linked = _temp.Combine("linked wt åäö");
        await git.GitAsync("worktree", "add", "-q", "-b", "feature/åäö", linked);
        var repo = new Repo(git, linked, first, second);
        if (prepare is not null)
            await prepare(repo);

        _methods = new SidecarMethods(
            new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) },
            new SidecarDependencies { Notify = (method, parameters) => _host.Host.NotifyAsync(method, parameters), Running = new FakeProcessTable() });
        _host = new HostHarness(_methods.Dispatch);
        Assert.True((await RequestAsync("repo.open", new { path = git.Path })).TryGetProperty("result", out _));
        return repo;
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

    [Fact]
    public async Task Check_name_says_why_a_name_cannot_be_used_and_null_when_it_can()
    {
        var repo = await OpenAsync();

        var taken = Result(await RequestAsync("ref.checkName", new { repoId = "1", worktree = repo.Git.Path, kind = "Branch", name = "side" }));
        var free = Result(await RequestAsync("ref.checkName", new { repoId = "1", worktree = repo.Git.Path, kind = "Tag", name = "v1.0" }));

        Assert.Equal("There is already a branch named side.", taken.GetProperty("problem").GetString());
        Assert.Equal(JsonValueKind.Null, free.GetProperty("problem").ValueKind);
    }

    [Fact]
    public async Task Create_branch_and_switch_in_a_linked_worktree()
    {
        var repo = await OpenAsync();

        var result = Result(await RequestAsync("branch.create", new { repoId = "1", worktree = repo.Linked, commit = repo.First, name = "fix/räksmörgås", @switch = true }));

        Assert.Equal("refs/heads/fix/räksmörgås", result.GetProperty("ref").GetString());
        Assert.Equal("refs/heads/fix/räksmörgås", (await repo.Git.At(repo.Linked).GitAsync("symbolic-ref", "HEAD")).StandardOutput.Trim());
    }

    [Fact]
    public async Task A_switch_git_refuses_is_a_change_refused_error_with_gits_words()
    {
        var repo = await OpenAsync(r =>
        {
            r.Git.WriteFile("a.txt", "mine, not committed\n");
            return Task.CompletedTask;
        });

        var error = Error(await RequestAsync("branch.switch", new { repoId = "1", worktree = repo.Git.Path, branch = "side" }));

        Assert.Equal((RpcErrorCodes.For(ErrorKind.ChangeRefused), "ChangeRefused"), (error.GetProperty("code").GetInt32(), error.GetProperty("data").GetProperty("kind").GetString()));
        Assert.Contains("a.txt", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_then_undo_with_the_result_as_it_came()
    {
        var repo = await OpenAsync();

        var reset = Result(await RequestAsync("branch.reset", new { repoId = "1", worktree = repo.Git.Path, commit = repo.First, mode = "Mixed" }));
        var undone = Result(await RequestAsync("branch.undoReset", new { repoId = "1", worktree = repo.Git.Path, reset }));

        Assert.Equal(("main", repo.Second, repo.First, "Mixed"), (reset.GetProperty("branch").GetString(), reset.GetProperty("oldCommit").GetString(), reset.GetProperty("newCommit").GetString(), reset.GetProperty("mode").GetString()));
        Assert.Equal(repo.Second, undone.GetProperty("newCommit").GetString());
        Assert.Equal(repo.Second, await repo.Git.RevParseAsync("main"));
    }

    [Fact]
    public async Task Create_tag_answers_with_the_new_ref()
    {
        var repo = await OpenAsync();

        var result = Result(await RequestAsync("tag.create", new { repoId = "1", worktree = repo.Git.Path, commit = repo.Second, name = "v1.0" }));

        Assert.Equal(("refs/tags/v1.0", $"Created tag v1.0 at {repo.Second[..8]}."), (result.GetProperty("ref").GetString(), result.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Missing_or_mixed_up_params_are_refused_without_running_git()
    {
        var repo = await OpenAsync();
        var w = repo.Git.Path;

        var codes = new[]
        {
            await RequestAsync("branch.switch", new { repoId = "1", worktree = w, branch = "side", commit = repo.First }),
            await RequestAsync("branch.create", new { repoId = "1", worktree = w, commit = "HEAD", name = "x" }),
            await RequestAsync("branch.reset", new { repoId = "1", worktree = w, commit = repo.First }),
            await RequestAsync("branch.switch", new { repoId = "1", worktree = w, remoteBranch = "origin/a:b" }),
            await RequestAsync("ref.checkName", new { repoId = "1", worktree = w, name = "x" }),
        }.Select(r => Error(r).GetProperty("code").GetInt32());

        Assert.All(codes, code => Assert.Equal(RpcErrorCodes.InvalidParams, code));
        Assert.Equal(repo.Second, await repo.Git.RevParseAsync("HEAD"));
    }
}
