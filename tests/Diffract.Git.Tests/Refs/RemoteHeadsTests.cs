using Diffract.Fixtures;
using Diffract.Git.Refs;

namespace Diffract.Git.Tests.Refs;

/// <summary>Asking a remote for its branches, against a bare fixture repo in the temp folder.</summary>
public sealed class RemoteHeadsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GitRunner Runner(FixtureRepo repo) => RemoteHeads.RunnerFor(repo.Path, new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) });

    [Fact]
    public void Parse_reads_branch_names_and_ignores_tags_and_other_lines()
    {
        var output = "0123456789abcdef0123456789abcdef01234567\trefs/heads/main\n"
            + "0123456789abcdef0123456789abcdef01234568\trefs/heads/fix/räksmörgås 1\n"
            + "0123456789abcdef0123456789abcdef01234569\trefs/tags/v1\n\n";

        Assert.Equal(["fix/räksmörgås 1", "main"], RemoteHeads.Parse(output).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_listing_keeps_the_commit_of_each_branch()
    {
        var output = "0123456789abcdef0123456789abcdef01234567\trefs/heads/main\n0123456789abcdef0123456789abcdef01234569\trefs/tags/v1\n";

        Assert.Equal(new Dictionary<string, string> { ["main"] = "0123456789abcdef0123456789abcdef01234567" }, new RemoteListing(output).Heads);
    }

    [Theory]
    [InlineData("ref: refs/heads/main\tHEAD\n0123456789abcdef0123456789abcdef01234567\tHEAD\n", "main")]
    [InlineData("ref: refs/heads/release/räksmörgås 1\tHEAD\n0123456789abcdef0123456789abcdef01234567\tHEAD\n", "release/räksmörgås 1")]
    [InlineData("0123456789abcdef0123456789abcdef01234567\tHEAD\n", null)]
    [InlineData("", null)]
    public void The_listing_names_the_branch_the_remote_head_points_at(string output, string? expected)
    {
        Assert.Equal(expected, new RemoteListing(output).DefaultBranch);
    }

    [Fact]
    public async Task Reads_the_default_branch_of_the_remote_and_the_commit_of_each_branch()
    {
        var remote = await FixtureRepo.InitAsync(_temp, "remote.git", bare: true);
        var mine = await FixtureRepo.InitAsync(_temp, "mine");
        var first = await mine.CommitAsync("a.txt", "a\n", "First");
        await mine.GitAsync("remote", "add", "origin", remote.Path);
        await mine.GitAsync("push", "-q", "origin", "main", "main:fix-1");
        await remote.GitAsync("symbolic-ref", "HEAD", "refs/heads/main");

        Assert.Equal("main", await RemoteHeads.ReadDefaultAsync(Runner(mine), "origin", Ct));
        Assert.Equal(first, (await RemoteHeads.ReadWithIdsAsync(Runner(mine), "origin", Ct))["fix-1"]);
    }

    [Fact]
    public async Task Reads_the_branches_the_remote_has_and_changes_nothing()
    {
        var remote = await FixtureRepo.InitAsync(_temp, "remote.git", bare: true);
        var mine = await FixtureRepo.InitAsync(_temp, "mine");
        await mine.CommitAsync("a.txt", "a\n", "First");
        await mine.GitAsync("remote", "add", "origin", remote.Path);
        await mine.GitAsync("push", "-q", "origin", "main", "main:fix-1");
        var refsBefore = (await mine.GitAsync("for-each-ref")).StandardOutput;

        var heads = await RemoteHeads.ReadAsync(Runner(mine), "origin", Ct);

        Assert.Equal(["fix-1", "main"], heads.Order());
        Assert.Equal(refsBefore, (await mine.GitAsync("for-each-ref")).StandardOutput);
    }

    [Fact]
    public async Task A_remote_that_cannot_be_reached_says_so()
    {
        var mine = await FixtureRepo.InitAsync(_temp, "mine");
        await mine.GitAsync("remote", "add", "origin", _temp.Combine("gone.git"));

        var error = await Assert.ThrowsAsync<RemoteFailedException>(() => RemoteHeads.ReadAsync(Runner(mine), "origin", Ct));

        Assert.Contains("nothing was removed", error.Reason);
    }
}
