using System.ComponentModel;
using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Leftovers;
using Diffract.Sidecar.Rpc;
using Microsoft.Extensions.Time.Testing;

namespace Diffract.Sidecar.Tests;

/// <summary>repo.open, repo.refresh and repo.close against fixture repos, through the same dispatch the host uses.</summary>
public sealed class RepoMethodsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(FixtureRepoBuilder.DefaultNow);

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SidecarMethods Methods(string gitExecutable = "git") => new(new GitRunnerOptions
    {
        Environment = IsolatedGit.Environment(_temp),
        GitExecutable = gitExecutable,
        TimeProvider = _time,
    });

    private Task<string> ScenarioAsync(string name) =>
        new FixtureRepoBuilder(_temp).BuildAsync(name).ContinueWith(t => t.Result.RepoPath, TaskScheduler.Default);

    private static Task<JsonElement> CallAsync(SidecarMethods methods, string method, object parameters) =>
        methods.Dispatch(method, JsonSerializer.SerializeToElement(parameters), Ct);

    private static string Id(JsonElement state) => state.GetProperty("repoId").GetString()!;

    [Fact]
    public async Task Open_returns_the_worktrees_and_leftovers_with_counts_that_add_up()
    {
        var repo = await ScenarioAsync("StaleAgentWorktree");

        var state = await CallAsync(Methods(), "repo.open", new { path = repo });

        Assert.Equal(Path.GetFullPath(repo), state.GetProperty("repoRoot").GetString(), ignoreCase: true);
        Assert.Equal("main", state.GetProperty("mainBranch").GetString());
        Assert.Equal(2, state.GetProperty("snapshot").GetProperty("worktrees").GetArrayLength());
        var leftovers = state.GetProperty("leftovers").EnumerateArray().ToList();
        Assert.Contains(leftovers, l => l.GetProperty("kind").GetString() == "Worktree" && l.GetProperty("agent").GetString() == "Claude Code");
        var summary = state.GetProperty("summary");
        Assert.Equal(leftovers.Count, summary.GetProperty("total").GetInt32());
        Assert.Equal(leftovers.Count, summary.GetProperty("safeToRemove").GetInt32() + summary.GetProperty("needsReview").GetInt32() + summary.GetProperty("inUse").GetInt32());
    }

    [Fact]
    public async Task Opening_the_same_repo_again_through_a_worktree_gives_the_same_id()
    {
        var repo = await ScenarioAsync("StaleAgentWorktree");
        var methods = Methods();

        var first = await CallAsync(methods, "repo.open", new { path = repo });
        var again = await CallAsync(methods, "repo.open", new { path = Path.Combine(repo, ".claude", "worktrees", "refactor-cart") });

        Assert.Equal(Id(first), Id(again));
        Assert.Equal(first.GetProperty("repoRoot").GetString(), again.GetProperty("repoRoot").GetString());
        Assert.Equal(1, methods.Repos.Count);
    }

    [Fact]
    public async Task Different_repos_get_different_ids()
    {
        var methods = Methods();

        var a = await CallAsync(methods, "repo.open", new { path = await ScenarioAsync("Clean") });
        var b = await CallAsync(methods, "repo.open", new { path = await ScenarioAsync("MasterAsMain") });

        Assert.NotEqual(Id(a), Id(b));
    }

    [Fact]
    public async Task Refresh_without_unreachable_keeps_the_last_count_and_its_age()
    {
        var repo = await ScenarioAsync("OrphanedCommit");
        var methods = Methods();
        var opened = await CallAsync(methods, "repo.open", new { path = repo });
        var countedAt = opened.GetProperty("unreachableCountedAt").GetDateTimeOffset();
        await IsolatedGit.WritableRunner(_temp, repo).RunAsync(["commit-tree", "HEAD^{tree}", "-m", "Another experiment"], Ct);
        _time.Advance(TimeSpan.FromMinutes(5));

        var refreshed = await CallAsync(methods, "repo.refresh", new { repoId = Id(opened), includeUnreachable = false });

        Assert.Equal(3, refreshed.GetProperty("snapshot").GetProperty("unreachableCommits").GetInt32());
        Assert.Equal(countedAt, refreshed.GetProperty("unreachableCountedAt").GetDateTimeOffset());
        Assert.Equal(countedAt.AddMinutes(5), refreshed.GetProperty("takenAt").GetDateTimeOffset());
        Assert.Equal(
            opened.GetProperty("leftovers").EnumerateArray().Count(l => l.GetProperty("kind").GetString() == "OrphanedCommit"),
            refreshed.GetProperty("leftovers").EnumerateArray().Count(l => l.GetProperty("kind").GetString() == "OrphanedCommit"));
    }

    [Fact]
    public async Task Refresh_with_unreachable_counts_them_again()
    {
        var repo = await ScenarioAsync("OrphanedCommit");
        var methods = Methods();
        var opened = await CallAsync(methods, "repo.open", new { path = repo });
        await IsolatedGit.WritableRunner(_temp, repo).RunAsync(["commit-tree", "HEAD^{tree}", "-m", "Another experiment"], Ct);
        _time.Advance(TimeSpan.FromMinutes(5));

        var refreshed = await CallAsync(methods, "repo.refresh", new { repoId = Id(opened), includeUnreachable = true });

        Assert.Equal(4, refreshed.GetProperty("snapshot").GetProperty("unreachableCommits").GetInt32());
        Assert.Equal(refreshed.GetProperty("takenAt").GetDateTimeOffset(), refreshed.GetProperty("unreachableCountedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_closed_repo_can_no_longer_be_refreshed()
    {
        var methods = Methods();
        var opened = await CallAsync(methods, "repo.open", new { path = await ScenarioAsync("Clean") });

        var closed = await CallAsync(methods, "repo.close", new { repoId = Id(opened) });
        var error = await Assert.ThrowsAsync<RpcException>(() => CallAsync(methods, "repo.refresh", new { repoId = Id(opened) }));

        Assert.Equal(JsonValueKind.Null, closed.ValueKind);
        Assert.Equal(RpcErrorCodes.InvalidParams, error.Code);
        Assert.Contains("repo.open", error.Message);
        Assert.Equal(0, methods.Repos.Count);
    }

    [Theory]
    [InlineData("repo.refresh", """{"repoId":"42"}""")]
    [InlineData("repo.close", """{"repoId":"42"}""")]
    [InlineData("repo.open", """{}""")]
    [InlineData("repo.open", """{"path":""}""")]
    [InlineData("repo.open", """{"path":42}""")]
    [InlineData("repo.open", """[]""")]
    [InlineData("repo.refresh", """{"includeUnreachable":true}""")]
    public async Task Missing_or_wrong_params_are_invalid_params(string method, string parameters)
    {
        var error = await Assert.ThrowsAsync<RpcException>(() => Methods().Dispatch(method, JsonDocument.Parse(parameters).RootElement, Ct));

        Assert.Equal((RpcErrorCodes.InvalidParams, null), (error.Code, error.Kind));
    }

    [Fact]
    public async Task No_params_at_all_is_invalid_params()
    {
        var error = await Assert.ThrowsAsync<RpcException>(() => Methods().Dispatch("repo.open", null, Ct));

        Assert.Equal(RpcErrorCodes.InvalidParams, error.Code);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("missing")]
    public async Task A_folder_that_is_not_a_repo_is_a_not_a_repo_error(string folder)
    {
        Directory.CreateDirectory(_temp.Combine("plain"));

        var error = await Assert.ThrowsAsync<RpcException>(() => CallAsync(Methods(), "repo.open", new { path = _temp.Combine(folder) }));

        Assert.Equal((-32001, ErrorKind.NotARepo), (error.Code, error.Kind));
        Assert.StartsWith("Not a git repository:", error.Message);
    }

    [Fact]
    public async Task Old_git_is_a_git_too_old_error()
    {
        var fakeGit = _temp.Combine("old-git.cmd");
        File.WriteAllText(fakeGit, "@echo git version 2.39.5.windows.1\r\n");

        var error = await Assert.ThrowsAsync<RpcException>(async () => await CallAsync(Methods(fakeGit), "repo.open", new { path = await ScenarioAsync("Clean") }));

        Assert.Equal((-32002, ErrorKind.GitTooOld), (error.Code, error.Kind));
        Assert.Contains("2.39.5", error.Message);
    }

    [Fact]
    public async Task Missing_git_is_a_git_failed_error_that_says_so()
    {
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await CallAsync(Methods(_temp.Combine("no-such-git.exe")), "repo.open", new { path = await ScenarioAsync("Clean") }));

        Assert.Equal((-32003, ErrorKind.GitFailed), (error.Code, error.Kind));
        Assert.StartsWith("Git was not found", error.Message);
    }

    [Fact]
    public async Task Cancelling_an_open_stops_git_and_answers_cancelled()
    {
        // A "git" that never answers, so the request is surely still running when the cancel arrives.
        var hangingGit = _temp.Combine("hanging-git.cmd");
        File.WriteAllText(hangingGit, "@ping -n 120 127.0.0.1 > nul\r\n");
        var methods = Methods(hangingGit);
        await using var host = new HostHarness(methods.Dispatch);
        var path = JsonSerializer.Serialize(_temp.Path);

        host.Send($$$"""{"jsonrpc":"2.0","id":1,"method":"repo.open","params":{"path":{{{path}}}}}""");
        await Task.Delay(500, Ct);
        host.Send("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":1}}""");

        var response = await host.NextJsonAsync();
        Assert.Equal(RpcErrorCodes.RequestCancelled, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Open_over_the_protocol_answers_with_the_repo_state()
    {
        var repo = await ScenarioAsync("Clean");
        await using var host = new HostHarness(Methods().Dispatch);

        host.Send($$$"""{"jsonrpc":"2.0","id":1,"method":"repo.open","params":{"path":{{{JsonSerializer.Serialize(repo)}}}}}""");

        var response = await host.NextJsonAsync();
        Assert.Equal("1", response.GetProperty("result").GetProperty("repoId").GetString());
        Assert.Equal(0, response.GetProperty("result").GetProperty("summary").GetProperty("total").GetInt32());
    }

    public static TheoryData<Exception, int?, string?> Exceptions => new()
    {
        { new NotAGitRepositoryException("C:\\src"), -32001, "NotARepo" },
        { new GitVersionTooOldException(new GitVersion(2, 39, 0)), -32002, "GitTooOld" },
        { new GitCommandException(["status"], 128, "fatal: bad"), -32003, "GitFailed" },
        { new Win32Exception(2), -32003, "GitFailed" },
        { new GitTimeoutException(["fsck"], TimeSpan.FromSeconds(30)), -32004, "TimedOut" },
        { new ConfigException("staleAfterDays must be at least 1."), -32005, "ConfigInvalid" },
        { new ReadOnlyViolationException(["push"], "'push' is not on the read-only allowlist"), null, null },
        { new InvalidOperationException("a bug"), null, null },
    };

    [Theory]
    [MemberData(nameof(Exceptions), DisableDiscoveryEnumeration = true)]
    public void Diffract_exceptions_get_their_own_code_and_kind_and_bugs_stay_internal_errors(Exception exception, int? code, string? kind)
    {
        var rpc = Methods().ToRpcException(exception);

        Assert.Equal((code, kind), (rpc?.Code, rpc?.Kind?.ToString()));
        if (rpc is not null && exception is not Win32Exception)
            Assert.Equal(exception.Message, rpc.Message);
    }
}
