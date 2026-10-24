using System.Collections.Concurrent;
using Diffract.Fixtures;
using Diffract.Git.Changes;

namespace Diffract.Git.Tests.Changes;

/// <summary>
/// Fetch, pull and push between fixture repos: "mine", where Diffract works, "theirs", where someone else pushes,
/// and a bare repo in the temp folder standing in for the remote both share.
/// </summary>
public sealed class RemoteSyncTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConcurrentQueue<GitCall> _calls = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GitRunnerOptions Options => new() { Environment = IsolatedGit.Environment(_temp), OnCall = _calls.Enqueue };

    private RemoteSync SyncFor(FixtureRepo repo) => new(repo.Path, Options);

    /// <summary>Mine and theirs, both on main with one commit, which the remote has too.</summary>
    private async Task<(FixtureRepo Mine, FixtureRepo Theirs, FixtureRepo Remote)> SharedAsync()
    {
        var remote = await FixtureRepo.InitAsync(_temp, "remote.git", bare: true);
        var mine = await FixtureRepo.InitAsync(_temp, "mine");
        await mine.CommitAsync("a.txt", "a\n", "First");
        await mine.GitAsync("remote", "add", "origin", remote.Path);
        await mine.GitAsync("push", "-q", "-u", "origin", "main");
        await mine.GitAsync("clone", "-q", remote.Path, _temp.Combine("theirs"));
        _calls.Clear();
        return (mine, mine.At(_temp.Combine("theirs")), remote);
    }

    private static async Task PushedElsewhereAsync(FixtureRepo theirs, int commits = 1)
    {
        for (var i = 0; i < commits; i++)
            await theirs.CommitAsync("theirs.txt", $"theirs {i}\n", $"Theirs {i}");
        await theirs.GitAsync("push", "-q", "origin", "main");
    }

    private static Task<RemoteFailedException> FailsAsync(Func<CancellationToken, Task<RemoteResult>> run) =>
        Assert.ThrowsAsync<RemoteFailedException>(() => run(Ct));

    private IEnumerable<string> Subcommands => _calls.Select(c => c.Arguments[0]);

    [Fact]
    public async Task Fetch_brings_new_commits_and_leaves_the_branch_where_it_was()
    {
        var (mine, theirs, _) = await SharedAsync();
        var head = await mine.RevParseAsync("HEAD");
        await PushedElsewhereAsync(theirs);

        var result = await SyncFor(mine).FetchAsync(Ct);

        Assert.Equal(new RemoteResult(RemoteOutcome.Done, "Fetched from origin."), result);
        Assert.Equal((await theirs.RevParseAsync("HEAD"), head), (await mine.RevParseAsync("origin/main"), await mine.RevParseAsync("HEAD")));
        Assert.Contains(_calls, c => c.Arguments.SequenceEqual(["fetch", "origin"]));
    }

    [Fact]
    public async Task Fetch_without_an_upstream_fetches_origin()
    {
        var (mine, _, _) = await SharedAsync();
        await mine.GitAsync("switch", "-q", "-c", "topic");

        var result = await SyncFor(mine).FetchAsync(Ct);

        Assert.Equal("Fetched from origin.", result.Message);
    }

    [Fact]
    public async Task Pull_fast_forwards_a_branch_that_is_only_behind()
    {
        var (mine, theirs, _) = await SharedAsync();
        await PushedElsewhereAsync(theirs, commits: 2);

        var result = await SyncFor(mine).PullAsync(Ct);

        Assert.Equal(new RemoteResult(RemoteOutcome.Done, "Pulled 2 commits from origin/main."), result);
        var pulled = (await mine.RevParseAsync("HEAD"), (await File.ReadAllTextAsync(Path.Combine(mine.Path, "theirs.txt"), Ct)).ReplaceLineEndings("\n"));
        Assert.Equal((await theirs.RevParseAsync("HEAD"), "theirs 1\n"), pulled);
        Assert.Contains(_calls, c => c.Arguments.SequenceEqual(["merge", "--ff-only", "refs/remotes/origin/main"]));
    }

    [Fact]
    public async Task Pull_with_nothing_new_is_up_to_date()
    {
        var (mine, _, _) = await SharedAsync();

        var result = await SyncFor(mine).PullAsync(Ct);

        Assert.Equal(new RemoteResult(RemoteOutcome.UpToDate, "Nothing to pull. main has everything on origin/main."), result);
        Assert.DoesNotContain("merge", Subcommands);
    }

    [Fact]
    public async Task Pull_does_nothing_when_the_branch_has_diverged()
    {
        var (mine, theirs, _) = await SharedAsync();
        await PushedElsewhereAsync(theirs);
        var head = await mine.CommitAsync("mine.txt", "mine\n", "Mine");

        var result = await SyncFor(mine).PullAsync(Ct);

        Assert.Equal(RemoteOutcome.Diverged, result.Outcome);
        Assert.Equal("main and origin/main have both moved on: 1 commit here and 1 commit there. Pull only fast-forwards for now, so nothing was changed.", result.Message);
        Assert.Equal(head, await mine.RevParseAsync("HEAD"));
        Assert.DoesNotContain("merge", Subcommands);
    }

    [Fact]
    public async Task Pull_that_would_overwrite_an_uncommitted_change_is_refused_and_changes_nothing()
    {
        var (mine, theirs, _) = await SharedAsync();
        await theirs.CommitAsync("a.txt", "theirs\n", "Theirs");
        await theirs.GitAsync("push", "-q", "origin", "main");
        var head = await mine.RevParseAsync("HEAD");
        mine.WriteFile("a.txt", "mine, not committed\n");

        var e = await FailsAsync(SyncFor(mine).PullAsync);

        Assert.StartsWith("Git did not pull, so nothing was changed.", e.Message, StringComparison.Ordinal);
        Assert.Contains("a.txt", e.GitOutput, StringComparison.Ordinal);
        var kept = (await mine.RevParseAsync("HEAD"), await File.ReadAllTextAsync(Path.Combine(mine.Path, "a.txt"), Ct));
        Assert.Equal((head, "mine, not committed\n"), kept);
    }

    [Fact]
    public async Task Pull_needs_an_upstream()
    {
        var (mine, _, _) = await SharedAsync();
        await mine.GitAsync("switch", "-q", "-c", "topic");

        var e = await FailsAsync(SyncFor(mine).PullAsync);

        Assert.Equal("topic does not follow a branch on a remote, so there is nothing to pull. Push it first.", e.Message);
        Assert.DoesNotContain("fetch", Subcommands);
    }

    [Fact]
    public async Task The_first_push_of_a_branch_sends_it_to_origin_and_sets_its_upstream()
    {
        var (mine, _, remote) = await SharedAsync();
        await mine.GitAsync("switch", "-q", "-c", "topic");
        var head = await mine.CommitAsync("topic.txt", "topic\n", "Topic");

        var result = await SyncFor(mine).PushAsync(Ct);

        Assert.Equal(new RemoteResult(RemoteOutcome.Done, "Pushed topic to origin/topic, which it now follows."), result);
        var upstream = (await mine.GitAsync("config", "branch.topic.merge")).StandardOutput.Trim();
        Assert.Equal((head, "refs/heads/topic"), (await remote.RevParseAsync("refs/heads/topic"), upstream));
        Assert.Contains(_calls, c => c.Arguments.SequenceEqual(["push", "--porcelain", "--set-upstream", "origin", "topic"]));
    }

    [Fact]
    public async Task Push_sends_new_commits_to_the_upstream()
    {
        var (mine, _, remote) = await SharedAsync();
        var head = await mine.CommitAsync("a.txt", "aa\n", "Second");

        var result = await SyncFor(mine).PushAsync(Ct);

        Assert.Equal(new RemoteResult(RemoteOutcome.Done, "Pushed main to origin/main."), result);
        Assert.Equal(head, await remote.RevParseAsync("refs/heads/main"));
        Assert.Contains(_calls, c => c.Arguments.SequenceEqual(["push", "--porcelain", "origin", "main"]));
    }

    [Fact]
    public async Task Push_with_nothing_new_is_up_to_date()
    {
        var (mine, _, _) = await SharedAsync();

        var result = await SyncFor(mine).PushAsync(Ct);

        Assert.Equal(new RemoteResult(RemoteOutcome.UpToDate, "Nothing to push. origin/main already has everything on main."), result);
    }

    [Fact]
    public async Task Push_is_rejected_when_the_remote_has_moved_on_and_the_remote_keeps_its_commits()
    {
        var (mine, theirs, remote) = await SharedAsync();
        await PushedElsewhereAsync(theirs);
        await mine.CommitAsync("mine.txt", "mine\n", "Mine");

        var result = await SyncFor(mine).PushAsync(Ct);

        Assert.Equal(new RemoteResult(RemoteOutcome.Rejected, "origin/main has commits that main lacks, so git did not push. Pull first, then push."), result);
        Assert.Equal(await theirs.RevParseAsync("HEAD"), await remote.RevParseAsync("refs/heads/main"));
    }

    [Fact]
    public async Task Push_refuses_a_branch_that_follows_one_of_another_name()
    {
        var (mine, _, _) = await SharedAsync();
        await mine.GitAsync("switch", "-q", "-c", "topic", "--track", "origin/main");

        var e = await FailsAsync(SyncFor(mine).PushAsync);

        Assert.StartsWith("topic follows origin/main, which has another name.", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("push", Subcommands);
    }

    [Fact]
    public async Task Push_and_pull_refuse_a_detached_head()
    {
        var (mine, _, _) = await SharedAsync();
        await mine.GitAsync("switch", "-q", "--detach");

        Assert.Equal("No branch with commits is checked out here, so there is nothing to push.", (await FailsAsync(SyncFor(mine).PushAsync)).Message);
        Assert.Equal("No branch with commits is checked out here, so there is nothing to pull.", (await FailsAsync(SyncFor(mine).PullAsync)).Message);
        Assert.DoesNotContain(Subcommands, s => s is "push" or "fetch");
    }

    [Fact]
    public async Task A_repo_without_origin_says_so()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "alone");
        await repo.CommitAsync("a.txt", "a\n", "First");

        var e = await FailsAsync(SyncFor(repo).PushAsync);

        Assert.Equal("This repo has no remote named origin, so there is nowhere to push to.", e.Message);
    }

    [Fact]
    public async Task A_remote_that_cannot_be_reached_gives_a_plain_message()
    {
        var (mine, _, _) = await SharedAsync();
        await mine.GitAsync("remote", "set-url", "origin", _temp.Combine("gone.git"));

        var e = await FailsAsync(SyncFor(mine).FetchAsync);

        Assert.Equal("Git could not fetch from origin. Check the network, and that you can sign in. This is what git said:", e.Reason);
        Assert.Contains("gone.git", e.GitOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_remote_that_does_not_answer_is_given_up_within_the_timeout()
    {
        var (mine, _, _) = await SharedAsync();
        await mine.GitAsync("remote", "set-url", "origin", "ssh://example.invalid/repo.git");
        await mine.GitAsync("config", "core.sshCommand", "sleep 60 #");
        // Git adds the host and command after it, which the "#" turns into a comment.
        var started = TimeProvider.System.GetTimestamp();

        await Assert.ThrowsAsync<GitTimeoutException>(() => new RemoteSync(mine.Path, Options, TimeSpan.FromSeconds(1)).FetchAsync(Ct));

        Assert.True(TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Every_write_is_on_the_changes_allowlist()
    {
        var (mine, theirs, _) = await SharedAsync();
        await PushedElsewhereAsync(theirs);
        var sync = SyncFor(mine);
        await sync.PullAsync(Ct);
        await mine.CommitAsync("mine.txt", "mine\n", "Mine");
        await sync.PushAsync(Ct);
        await sync.FetchAsync(Ct);

        Assert.All(_calls, c => Assert.True(ChangesPolicy.IsAllowed(c.Arguments, out _) || ReadOnlyPolicy.IsAllowed(c.Arguments, out _), string.Join(' ', c.Arguments)));
        Assert.Equal(["fetch", "fetch", "merge", "push"], _calls.Where(c => !ReadOnlyPolicy.IsAllowed(c.Arguments, out _)).Select(c => c.Arguments[0]).Order());
    }
}
