using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// remote.fetch, remote.pull and remote.push over the real host. The repo, a linked worktree and a second clone,
/// "theirs", share a bare remote in the temp folder. Every change to them is made before the repo is opened.
/// </summary>
public sealed class RemoteMethodsTests : IAsyncDisposable
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

    private sealed record Repos(FixtureRepo Repo, string Linked, FixtureRepo Theirs);

    private async Task<Repos> OpenAsync(Func<Repos, Task>? prepare = null)
    {
        var remote = await FixtureRepo.InitAsync(_temp, "remote.git", bare: true);
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        await repo.CommitAsync("readme.md", "hello\n", "Initial commit");
        await repo.GitAsync("remote", "add", "origin", remote.Path);
        await repo.GitAsync("push", "-q", "-u", "origin", "main");
        await repo.GitAsync("clone", "-q", remote.Path, _temp.Combine("theirs"));
        var linked = _temp.Combine("linked wt åäö");
        await repo.GitAsync("worktree", "add", "-q", "-b", "feature/åäö", linked);
        var repos = new Repos(repo, linked, repo.At(_temp.Combine("theirs")));
        if (prepare is not null)
            await prepare(repos);

        _methods = new SidecarMethods(
            new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) },
            new SidecarDependencies { Notify = (method, parameters) => _host.Host.NotifyAsync(method, parameters), Running = new FakeProcessTable() });
        _host = new HostHarness(_methods.Dispatch);
        Assert.True((await RequestAsync("repo.open", new { path = repo.Path })).TryGetProperty("result", out _));
        return repos;
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        var id = _nextId++;
        _host.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        return await AnswerAsync(id);
    }

    /// <summary>The response to <paramref name="id"/>, skipping the notifications that arrive first.</summary>
    private async Task<JsonElement> AnswerAsync(int id)
    {
        while (true)
        {
            var message = await _host.NextJsonAsync();
            if (message.TryGetProperty("id", out var answered) && answered.GetInt32() == id)
                return message;
        }
    }

    private static JsonElement Result(JsonElement response) =>
        response.TryGetProperty("result", out var result) ? result : throw new InvalidOperationException(response.ToString());

    private static async Task PushedElsewhereAsync(FixtureRepo theirs)
    {
        await theirs.CommitAsync("theirs.txt", "theirs\n", "Theirs");
        await theirs.GitAsync("push", "-q", "origin", "main");
    }

    [Fact]
    public async Task The_first_push_from_a_linked_worktree_answers_with_what_happened_and_the_new_status()
    {
        var repos = await OpenAsync(r => r.Repo.At(r.Linked).CommitAsync("feature.txt", "åäö\n", "Feature"));

        var result = Result(await RequestAsync("remote.push", new { repoId = "1", worktree = repos.Linked }));

        Assert.Equal("Done", result.GetProperty("outcome").GetString());
        Assert.Equal("Pushed feature/åäö to origin/feature/åäö, which it now follows.", result.GetProperty("message").GetString());
        var status = result.GetProperty("status");
        Assert.Equal(("origin/feature/åäö", 0), (status.GetProperty("upstream").GetString(), status.GetProperty("ahead").GetInt32()));
        Assert.Equal("origin/feature/åäö", status.GetProperty("headOnRemote").GetString());
    }

    [Fact]
    public async Task A_named_operation_gets_git_output_as_notifications_before_its_answer()
    {
        var repos = await OpenAsync(r => PushedElsewhereAsync(r.Theirs));

        _host.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 50, method = "remote.fetch", @params = new { repoId = "1", worktree = repos.Repo.Path, operation = "op-7" } }));
        var outputs = new List<JsonElement>();
        JsonElement message;
        while (!(message = await _host.NextJsonAsync()).TryGetProperty("id", out _))
            if (message.GetProperty("method").GetString() == "command.output")
                outputs.Add(message.GetProperty("params"));

        Assert.Equal("Done", Result(message).GetProperty("outcome").GetString());
        Assert.All(outputs, o => Assert.Equal("op-7", o.GetProperty("operation").GetString()));
        Assert.Equal(("Command", "git fetch --progress origin"), (outputs[0].GetProperty("kind").GetString(), outputs[0].GetProperty("text").GetString()));
        Assert.Contains(outputs, o => o.GetProperty("kind").GetString() == "Line" && o.GetProperty("text").GetString()!.StartsWith("From ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_an_operation_nothing_is_sent_and_git_reports_no_progress()
    {
        var repos = await OpenAsync(r => PushedElsewhereAsync(r.Theirs));

        _host.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 51, method = "remote.fetch", @params = new { repoId = "1", worktree = repos.Repo.Path } }));
        var methods = new List<string?>();
        JsonElement message;
        while (!(message = await _host.NextJsonAsync()).TryGetProperty("id", out _))
            methods.Add(message.GetProperty("method").GetString());

        Assert.Equal("Done", Result(message).GetProperty("outcome").GetString());
        Assert.DoesNotContain("command.output", methods);
    }

    [Fact]
    public async Task Pull_on_a_diverged_branch_answers_diverged_with_both_counts()
    {
        var repos = await OpenAsync(async r =>
        {
            await PushedElsewhereAsync(r.Theirs);
            await r.Repo.CommitAsync("mine.txt", "mine\n", "Mine");
        });

        var result = Result(await RequestAsync("remote.pull", new { repoId = "1", worktree = repos.Repo.Path }));

        Assert.Equal("Diverged", result.GetProperty("outcome").GetString());
        var status = result.GetProperty("status");
        Assert.Equal((1, 1), (status.GetProperty("ahead").GetInt32(), status.GetProperty("behind").GetInt32()));
    }

    [Fact]
    public async Task Fetch_then_pull_brings_the_branch_up_to_its_upstream()
    {
        var repos = await OpenAsync(r => PushedElsewhereAsync(r.Theirs));

        var fetched = Result(await RequestAsync("remote.fetch", new { repoId = "1", worktree = repos.Repo.Path }));
        var pulled = Result(await RequestAsync("remote.pull", new { repoId = "1", worktree = repos.Repo.Path }));

        Assert.Equal(1, fetched.GetProperty("status").GetProperty("behind").GetInt32());
        Assert.Equal(("Done", 0), (pulled.GetProperty("outcome").GetString(), pulled.GetProperty("status").GetProperty("behind").GetInt32()));
        Assert.Equal(await repos.Theirs.RevParseAsync("HEAD"), await repos.Repo.RevParseAsync("HEAD"));
    }

    [Fact]
    public async Task A_remote_that_cannot_be_reached_is_a_remote_failed_error_in_plain_words()
    {
        var repos = await OpenAsync(r => r.Repo.GitAsync("remote", "set-url", "origin", _temp.Combine("gone.git")));

        var error = (await RequestAsync("remote.fetch", new { repoId = "1", worktree = repos.Repo.Path })).GetProperty("error");

        Assert.Equal((RpcErrorCodes.For(ErrorKind.RemoteFailed), "RemoteFailed"), (error.GetProperty("code").GetInt32(), error.GetProperty("data").GetProperty("kind").GetString()));
        Assert.StartsWith("Git could not fetch from origin.", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fetch_that_hangs_stops_when_the_request_is_cancelled()
    {
        var repos = await OpenAsync(async r =>
        {
            await r.Repo.GitAsync("remote", "set-url", "origin", "ssh://example.invalid/repo.git");
            await r.Repo.GitAsync("config", "core.sshCommand", "sleep 60 #");
        });
        var id = _nextId++;
        _host.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "remote.fetch", @params = new { repoId = "1", worktree = repos.Repo.Path } }));
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        _host.Send($$$"""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":{{{id}}}}}""");

        Assert.Equal(RpcErrorCodes.RequestCancelled, (await AnswerAsync(id)).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task A_folder_that_is_not_one_of_the_repos_worktrees_is_refused()
    {
        await OpenAsync();

        var error = (await RequestAsync("remote.push", new { repoId = "1", worktree = _temp.Combine("theirs") })).GetProperty("error");

        Assert.Equal(RpcErrorCodes.InvalidParams, error.GetProperty("code").GetInt32());
    }
}
