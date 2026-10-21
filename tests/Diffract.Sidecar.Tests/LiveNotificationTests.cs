using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Git.Watching;

namespace Diffract.Sidecar.Tests;

/// <summary>An open repo sends repo.changed over the protocol when it changes. Real file events and real time.</summary>
public sealed class LiveNotificationTests : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly StringWriter _log = new();
    private HostHarness _host = null!;
    private SidecarMethods _methods = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _methods.Dispose();
        _temp.Dispose();
    }

    private async Task<(string Repo, string Worktree)> OpenAsync()
    {
        var repo = (await new FixtureRepoBuilder(_temp).StaleAgentWorktreeAsync()).RepoPath;
        _methods = new SidecarMethods(
            new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) },
            notify: (method, parameters) => _host.Host.NotifyAsync(method, parameters),
            log: _log);
        _host = new HostHarness(_methods.Dispatch);

        _host.Send($$$"""{"jsonrpc":"2.0","id":1,"method":"repo.open","params":{"path":{{{JsonSerializer.Serialize(repo)}}}}}""");
        var opened = await _host.NextJsonAsync();
        Assert.True(opened.TryGetProperty("result", out _), opened.ToString());
        return (repo, Path.GetFullPath(Path.Combine(repo, ".claude", "worktrees", "refactor-cart")));
    }

    [Fact]
    public async Task A_file_change_in_a_worktree_is_sent_as_repo_changed_with_the_new_state()
    {
        var (_, worktree) = await OpenAsync();

        await File.WriteAllTextAsync(Path.Combine(worktree, "notes.txt"), "todo", Ct);

        var message = await _host.NextJsonAsync();
        Assert.Equal("repo.changed", message.GetProperty("method").GetString());
        var parameters = message.GetProperty("params");
        var change = Assert.Single(parameters.GetProperty("changes").EnumerateArray());
        Assert.Equal(("WorktreeFiles", worktree), (change.GetProperty("kind").GetString(), change.GetProperty("path").GetString()));
        var refreshed = parameters.GetProperty("state").GetProperty("snapshot").GetProperty("worktrees").EnumerateArray()
            .Single(w => w.GetProperty("path").GetString() == worktree);
        Assert.Equal(1, refreshed.GetProperty("dirtyFileCount").GetInt32());
        Assert.Equal("1", parameters.GetProperty("state").GetProperty("repoId").GetString());
        Assert.Empty(_log.ToString());
    }

    [Fact]
    public async Task A_dirty_worktree_turns_in_use_live()
    {
        var (_, worktree) = await OpenAsync();

        await File.WriteAllTextAsync(Path.Combine(worktree, "cart.txt"), "half done", Ct);

        var leftovers = (await _host.NextJsonAsync()).GetProperty("params").GetProperty("state").GetProperty("leftovers").EnumerateArray();
        var row = leftovers.Single(l => l.GetProperty("path").GetString() == worktree);
        Assert.Equal("InUse", row.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task A_closed_repo_sends_nothing_more()
    {
        var (_, worktree) = await OpenAsync();

        _host.Send("""{"jsonrpc":"2.0","id":2,"method":"repo.close","params":{"repoId":"1"}}""");
        Assert.Equal("""{"jsonrpc":"2.0","id":2,"result":null}""", await _host.NextAsync());
        await File.WriteAllTextAsync(Path.Combine(worktree, "notes.txt"), "todo", Ct);

        await Task.Delay(RefreshScheduler.MaxWait + TimeSpan.FromMilliseconds(500), Ct);
        Assert.Empty(_host.Drain());
    }
}
