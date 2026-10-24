using Diffract.Fixtures;
using Diffract.Git.Refs;

namespace Diffract.Git.Tests.Refs;

/// <summary>New names checked against a fixture repo with a main branch and a v1 tag.</summary>
public sealed class RefNamesTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private async Task<GitRunner> RepoAsync()
    {
        var repo = await FixtureRepo.InitAsync(_temp, "repo");
        await repo.CommitAsync("a.txt", "a\n", "First");
        await repo.GitAsync("tag", "v1");
        return IsolatedGit.ReadOnlyRunner(_temp, repo.Path);
    }

    private async Task<string?> ProblemAsync(RefNameKind kind, string name) =>
        await RefNames.ProblemAsync(await RepoAsync(), kind, name, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(RefNameKind.Branch, "feature/räksmörgås")]
    [InlineData(RefNameKind.Branch, "v1")]
    [InlineData(RefNameKind.Tag, "main")]
    [InlineData(RefNameKind.Tag, "v2.0")]
    public async Task A_new_name_git_allows_has_no_problem(RefNameKind kind, string name) => Assert.Null(await ProblemAsync(kind, name));

    [Theory]
    [InlineData(RefNameKind.Branch, "main", "There is already a branch named main.")]
    [InlineData(RefNameKind.Tag, "v1", "There is already a tag named v1.")]
    [InlineData(RefNameKind.Branch, " ", "A branch needs a name.")]
    [InlineData(RefNameKind.Branch, "-x", "A branch name cannot start with \"-\".")]
    [InlineData(RefNameKind.Tag, "+x", "A tag name cannot start with \"+\".")]
    public async Task A_taken_or_empty_name_says_why(RefNameKind kind, string name, string problem) => Assert.Equal(problem, await ProblemAsync(kind, name));

    [Theory]
    [InlineData("two words")]
    [InlineData("a..b")]
    [InlineData("ends.lock")]
    [InlineData("a:b")]
    [InlineData("star*")]
    public async Task A_name_git_does_not_allow_is_refused_by_gits_own_rules(string name) =>
        Assert.StartsWith($"\"{name}\" is not a name git allows for a branch.", await ProblemAsync(RefNameKind.Branch, name), StringComparison.Ordinal);
}
