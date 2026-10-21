using Diffract.Fixtures;

namespace Diffract.Git.Tests.Fixtures;

/// <summary>The generated history for performance tests has the shape it promises, and the same seed repeats it.</summary>
public sealed class LargeHistoryTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private async Task<string> GitAsync(string repo, params string[] args) =>
        (await IsolatedGit.ReadOnlyRunner(_temp, repo).RunAsync(args, TestContext.Current.CancellationToken)).StandardOutput.Trim();

    [Fact]
    public async Task Has_the_commits_merges_branches_and_tags_it_reports()
    {
        var repo = await LargeHistory.BuildAsync(_temp, "large", commits: 5000);

        Assert.Equal("5000", await GitAsync(repo.Path, "rev-list", "--count", "--all"));
        Assert.Equal(repo.Merges.ToString(), await GitAsync(repo.Path, "rev-list", "--count", "--merges", "--all"));
        Assert.InRange(repo.Merges, 100, 2000);
        Assert.Equal(repo.Branches, (await GitAsync(repo.Path, "for-each-ref", "refs/heads", "--format=%(refname)")).Split('\n').Length);
        Assert.InRange(repo.Branches, 2, 7);
        Assert.Equal(repo.Tags, (await GitAsync(repo.Path, "for-each-ref", "refs/tags", "--format=%(refname)")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal("main", await GitAsync(repo.Path, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal("", await GitAsync(repo.Path, "status", "--porcelain"));
    }

    [Fact]
    public async Task Keeps_non_ASCII_names_and_subjects()
    {
        var repo = await LargeHistory.BuildAsync(_temp, "names", commits: 300);

        var authors = await GitAsync(repo.Path, "log", "--all", "--format=%an");
        Assert.Contains("Åsa Öberg", authors);
        Assert.Contains("李雷", authors);
        Assert.Contains("Räkna moms rätt", await GitAsync(repo.Path, "log", "--all", "--format=%s"));
    }

    [Fact]
    public async Task The_same_seed_gives_the_same_commits()
    {
        var first = await LargeHistory.BuildAsync(_temp, "a", commits: 1000, seed: 7);
        var second = await LargeHistory.BuildAsync(_temp, "b", commits: 1000, seed: 7);
        var other = await LargeHistory.BuildAsync(_temp, "c", commits: 1000, seed: 8);

        Assert.Equal(await GitAsync(first.Path, "rev-parse", "main"), await GitAsync(second.Path, "rev-parse", "main"));
        Assert.NotEqual(await GitAsync(first.Path, "rev-parse", "main"), await GitAsync(other.Path, "rev-parse", "main"));
    }

    [Fact]
    public async Task Refuses_a_folder_outside_the_temp_folder()
    {
        using var elsewhere = new TempDirectory();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LargeHistory.BuildAsync(_temp, Path.GetRelativePath(_temp.Path, elsewhere.Combine("escape")), commits: 10));
        Assert.False(Directory.Exists(elsewhere.Combine("escape", ".git")));
    }
}
