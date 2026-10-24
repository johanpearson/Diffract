using Diffract.Fixtures;
using Diffract.Git.Refs;

namespace Diffract.Git.Tests.Refs;

/// <summary>Resets in a fixture repo: main has three commits, each changing a.txt, and an uncommitted b.txt.</summary>
public sealed class ResetWriterTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Repo(FixtureRepo Git, string First, string Third);

    private async Task<Repo> RepoAsync()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        var first = await repo.CommitAsync("a.txt", "1\n", "First");
        await repo.CommitAsync("a.txt", "2\n", "Second");
        var third = await repo.CommitAsync("a.txt", "3\n", "Third");
        repo.WriteFile("b.txt", "not committed\n");
        return new Repo(repo, first, third);
    }

    private ResetWriter WriterFor(Repo repo) => new(repo.Git.Path, new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) });

    /// <summary>The branch, HEAD, what is staged and what is not, and a.txt on disk.</summary>
    private static async Task<string> StateAsync(Repo repo)
    {
        var git = repo.Git;
        var parts = new[]
        {
            (await git.GitAsync("symbolic-ref", "HEAD")).StandardOutput,
            await git.RevParseAsync("HEAD"),
            (await git.GitAsync("diff", "--cached", "--name-status")).StandardOutput,
            (await git.GitAsync("status", "--porcelain")).StandardOutput,
            await File.ReadAllTextAsync(Path.Combine(git.Path, "a.txt"), Ct),
        };
        return string.Join("|", parts);
    }

    [Fact]
    public async Task Soft_reset_moves_the_branch_and_keeps_the_changes_staged()
    {
        var repo = await RepoAsync();

        var reset = await WriterFor(repo).ResetAsync(repo.First, ResetMode.Soft, Ct);

        Assert.Equal(new ResetResult { Branch = "main", OldCommit = repo.Third, NewCommit = repo.First, Mode = ResetMode.Soft }, reset);
        Assert.Equal(repo.First, await repo.Git.RevParseAsync("main"));
        Assert.Equal(("M\ta.txt\n", "3\n"), ((await repo.Git.GitAsync("diff", "--cached", "--name-status")).StandardOutput, await File.ReadAllTextAsync(Path.Combine(repo.Git.Path, "a.txt"), Ct)));
    }

    [Fact]
    public async Task Mixed_reset_keeps_the_changes_as_unstaged()
    {
        var repo = await RepoAsync();

        await WriterFor(repo).ResetAsync(repo.First, ResetMode.Mixed, Ct);

        Assert.Equal(" M a.txt\n?? b.txt\n", (await repo.Git.GitAsync("status", "--porcelain")).StandardOutput);
        Assert.Equal(repo.First, await repo.Git.RevParseAsync("main"));
    }

    [Theory]
    [InlineData(ResetMode.Soft)]
    [InlineData(ResetMode.Mixed)]
    public async Task Reset_then_undo_ends_where_it_started(ResetMode mode)
    {
        var repo = await RepoAsync();
        var before = await StateAsync(repo);
        var writer = WriterFor(repo);

        var undone = await writer.UndoAsync(await writer.ResetAsync(repo.First, mode, Ct), Ct);

        Assert.Equal(before, await StateAsync(repo));
        Assert.Equal((repo.First, repo.Third), (undone.OldCommit, undone.NewCommit));
    }

    [Fact]
    public async Task Undo_does_nothing_once_the_branch_has_moved_on()
    {
        var repo = await RepoAsync();
        var writer = WriterFor(repo);
        var reset = await writer.ResetAsync(repo.First, ResetMode.Mixed, Ct);
        var later = await repo.Git.CommitAsync("c.txt", "c\n", "Later");

        var e = await Assert.ThrowsAsync<ChangeRefusedException>(() => writer.UndoAsync(reset, Ct));

        Assert.Equal("main is no longer where the reset left it, so undo did nothing. Something has changed it since.", e.Message);
        Assert.Equal(later, await repo.Git.RevParseAsync("main"));
    }

    [Fact]
    public async Task A_detached_head_has_no_branch_to_reset()
    {
        var repo = await RepoAsync();
        await repo.Git.GitAsync("switch", "-q", "--detach");

        var e = await Assert.ThrowsAsync<ChangeRefusedException>(() => WriterFor(repo).ResetAsync(repo.First, ResetMode.Soft, Ct));

        Assert.Equal("No branch is checked out here, so there is no branch to reset.", e.Message);
    }

    [Fact]
    public async Task Reset_to_where_the_branch_already_is_does_nothing()
    {
        var repo = await RepoAsync();

        var e = await Assert.ThrowsAsync<ChangeRefusedException>(() => WriterFor(repo).ResetAsync(repo.Third, ResetMode.Soft, Ct));

        Assert.Equal($"main is already at {repo.Third[..8]}.", e.Message);
    }

    [Fact]
    public async Task Only_a_full_commit_id_is_taken() =>
        await Assert.ThrowsAsync<ArgumentException>(async () => await WriterFor(await RepoAsync()).ResetAsync("HEAD~1", ResetMode.Soft, Ct));
}
