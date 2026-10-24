using System.Collections.Concurrent;
using Diffract.Fixtures;
using Diffract.Git.Refs;

namespace Diffract.Git.Tests.Refs;

/// <summary>Branches, tags and switches in fixture repos: main with two commits, and a side branch on the first.</summary>
public sealed class RefWriterTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConcurrentQueue<GitCall> _calls = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GitRunnerOptions Options => new() { Environment = IsolatedGit.Environment(_temp), OnCall = _calls.Enqueue };

    private sealed record Repo(FixtureRepo Git, string First, string Second);

    private async Task<Repo> RepoAsync()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        var first = await repo.CommitAsync("a.txt", "a\n", "First");
        var second = await repo.CommitAsync("a.txt", "aa\n", "Second");
        await repo.GitAsync("branch", "side", first);
        _calls.Clear();
        return new Repo(repo, first, second);
    }

    private RefWriter WriterFor(Repo repo) => new(repo.Git.Path, Options);

    private static async Task<string> HeadAsync(Repo repo) => (await repo.Git.GitAsync("rev-parse", "--symbolic-full-name", "HEAD")).StandardOutput.Trim();

    private IEnumerable<string> Writes => _calls.Where(c => !ReadOnlyPolicy.IsAllowed(c.Arguments, out _)).Select(c => string.Join(' ', c.Arguments));

    [Fact]
    public async Task Create_branch_adds_the_branch_and_leaves_head_alone()
    {
        var repo = await RepoAsync();

        var change = await WriterFor(repo).CreateBranchAsync(new NewRef("feature/räksmörgås", repo.First), switchTo: false, Ct);

        Assert.Equal(new RefChange("refs/heads/feature/räksmörgås", $"Created branch feature/räksmörgås at {repo.First[..8]}."), change);
        Assert.Equal((repo.First, "refs/heads/main"), (await repo.Git.RevParseAsync("feature/räksmörgås"), await HeadAsync(repo)));
        Assert.Equal([$"branch feature/räksmörgås {repo.First}"], Writes);
    }

    [Fact]
    public async Task Create_and_switch_takes_uncommitted_changes_along_when_they_fit()
    {
        var repo = await RepoAsync();
        repo.Git.WriteFile("b.txt", "new, not committed\n");

        var change = await WriterFor(repo).CreateBranchAsync(new NewRef("topic", repo.Second), switchTo: true, Ct);

        Assert.Equal($"Created branch topic at {repo.Second[..8]}, and switched to it.", change.Message);
        Assert.Equal("refs/heads/topic", await HeadAsync(repo));
        Assert.True(File.Exists(Path.Combine(repo.Git.Path, "b.txt")));
    }

    [Fact]
    public async Task A_taken_branch_name_is_refused_before_git_writes()
    {
        var repo = await RepoAsync();

        var e = await Assert.ThrowsAsync<ChangeRefusedException>(() => WriterFor(repo).CreateBranchAsync(new NewRef("side", repo.Second), switchTo: true, Ct));

        Assert.Equal("There is already a branch named side.", e.Message);
        Assert.Empty(Writes);
    }

    [Fact]
    public async Task Create_tag_makes_an_annotated_tag_with_the_message()
    {
        var repo = await RepoAsync();

        var change = await WriterFor(repo).CreateTagAsync(new NewRef("v1.0", repo.First), "Första släppet\n", Ct);

        Assert.Equal(new RefChange("refs/tags/v1.0", $"Created tag v1.0 at {repo.First[..8]}."), change);
        var tag = (await repo.Git.GitAsync("for-each-ref", "--format=%(objecttype) %(*objectname) %(contents)", "refs/tags/v1.0")).StandardOutput;
        Assert.Equal($"tag {repo.First} Första släppet\n\n", tag);
    }

    [Fact]
    public async Task A_tag_without_a_message_has_its_name_as_message()
    {
        var repo = await RepoAsync();

        await WriterFor(repo).CreateTagAsync(new NewRef("v2", repo.Second), null, Ct);

        Assert.Equal("v2", (await repo.Git.GitAsync("for-each-ref", "--format=%(contents:subject)", "refs/tags/v2")).StandardOutput.Trim());
    }

    [Fact]
    public async Task Switch_to_a_local_branch()
    {
        var repo = await RepoAsync();

        var change = await WriterFor(repo).SwitchAsync(new SwitchTarget.Branch("side"), Ct);

        Assert.Equal(new RefChange("refs/heads/side", "Switched to side."), change);
        Assert.Equal("refs/heads/side", await HeadAsync(repo));
    }

    [Fact]
    public async Task Switch_that_would_overwrite_an_uncommitted_change_is_refused_and_changes_nothing()
    {
        var repo = await RepoAsync();
        repo.Git.WriteFile("a.txt", "mine, not committed\n");

        var e = await Assert.ThrowsAsync<ChangeRefusedException>(() => WriterFor(repo).SwitchAsync(new SwitchTarget.Branch("side"), Ct));

        Assert.StartsWith("Git did not switch, so nothing changed.", e.Message, StringComparison.Ordinal);
        Assert.Contains("a.txt", e.GitOutput, StringComparison.Ordinal);
        var kept = (await HeadAsync(repo), await File.ReadAllTextAsync(Path.Combine(repo.Git.Path, "a.txt"), Ct));
        Assert.Equal(("refs/heads/main", "mine, not committed\n"), kept);
    }

    [Fact]
    public async Task Switch_to_a_remote_tracking_branch_creates_a_local_branch_that_follows_it()
    {
        var repo = await RepoAsync();
        var remote = await FixtureRepo.InitAsync(_temp, "remote.git", bare: true);
        await repo.Git.GitAsync("remote", "add", "origin", remote.Path);
        await repo.Git.GitAsync("push", "-q", "origin", "side:topic");
        await repo.Git.GitAsync("fetch", "-q", "origin");

        var change = await WriterFor(repo).SwitchAsync(new SwitchTarget.RemoteBranch("origin/topic"), Ct);

        Assert.Equal(new RefChange("refs/heads/topic", "Created topic to follow origin/topic, and switched to it."), change);
        Assert.Equal("origin/topic", (await repo.Git.GitAsync("rev-parse", "--abbrev-ref", "topic@{upstream}")).StandardOutput.Trim());
    }

    [Fact]
    public async Task Check_out_a_commit_on_a_detached_head()
    {
        var repo = await RepoAsync();

        var change = await WriterFor(repo).SwitchAsync(new SwitchTarget.Commit(repo.First), Ct);

        Assert.Equal(new RefChange(null, $"Checked out {repo.First[..8]} on a detached HEAD."), change);
        Assert.Equal(("HEAD", repo.First), (await HeadAsync(repo), await repo.Git.RevParseAsync("HEAD")));
    }

    [Fact]
    public async Task A_commit_that_is_not_a_full_id_is_refused_before_git_runs()
    {
        var repo = await RepoAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => WriterFor(repo).SwitchAsync(new SwitchTarget.Commit("HEAD~1"), Ct));

        Assert.Empty(Writes);
    }

    [Fact]
    public async Task Every_write_is_on_the_changes_allowlist()
    {
        var repo = await RepoAsync();
        var writer = WriterFor(repo);

        await writer.CreateBranchAsync(new NewRef("a", repo.First), switchTo: false, Ct);
        await writer.CreateBranchAsync(new NewRef("b", repo.First), switchTo: true, Ct);
        await writer.CreateTagAsync(new NewRef("t", repo.First), "t", Ct);
        await writer.SwitchAsync(new SwitchTarget.Branch("main"), Ct);
        await writer.SwitchAsync(new SwitchTarget.Commit(repo.Second), Ct);

        Assert.All(_calls, c => Assert.True(ChangesPolicy.IsAllowed(c.Arguments, out _) || ReadOnlyPolicy.IsAllowed(c.Arguments, out _), string.Join(' ', c.Arguments)));
        Assert.Equal(["branch", "switch", "switch", "switch", "tag"], _calls.Where(c => !ReadOnlyPolicy.IsAllowed(c.Arguments, out _)).Select(c => c.Arguments[0]).Order());
    }
}
