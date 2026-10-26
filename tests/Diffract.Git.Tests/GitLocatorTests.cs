using System.ComponentModel;
using Diffract.Fixtures;

namespace Diffract.Git.Tests;

/// <summary>Finding git again when an update of Git for Windows has moved it while Diffract runs.</summary>
public sealed class GitLocatorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GitInstallation Gone => new(_temp.Combine("mingw64", "bin", "git.exe"), null);

    /// <summary>A locator that finds each installation in turn, and the last one from then on.</summary>
    private static (GitLocator Locator, Func<int> Finds) Finding(params GitInstallation[] answers)
    {
        var finds = 0;
        var locator = new GitLocator(() => answers[Math.Min(finds++, answers.Length - 1)]);
        return (locator, () => finds);
    }

    private GitRunner Runner(GitLocator locator) =>
        new(_temp.Path, new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp), Locator = locator });

    [Fact]
    public void Finds_git_once_up_front_and_again_only_when_asked()
    {
        var moved = new GitInstallation(@"C:\Program Files\Git\ucrt64\bin\git.exe", null);
        var (locator, finds) = Finding(Gone, moved);

        Assert.Equal((Gone, 1), (locator.Current, finds()));
        Assert.Equal(moved, locator.FindAgain());
        Assert.Equal((moved, 2), (locator.Current, finds()));
    }

    [Fact]
    public async Task A_git_that_moved_is_found_again_and_the_call_succeeds()
    {
        var real = GitInstallation.ForThisMachine();
        var (locator, finds) = Finding(Gone, real);

        var result = await Runner(locator).RunAsync(["version"], Ct);

        Assert.StartsWith("git version ", result.StandardOutput);
        Assert.Equal((real, 2), (locator.Current, finds()));
    }

    [Fact]
    public async Task Every_runner_on_the_same_options_starts_the_git_found_again()
    {
        var (locator, finds) = Finding(Gone, GitInstallation.ForThisMachine());
        await Runner(locator).RunAsync(["version"], Ct);

        await Runner(locator).RunAsync(["version"], Ct);

        Assert.Equal(2, finds());
    }

    [Fact]
    public async Task When_git_is_still_where_it_was_the_call_fails_after_looking_once()
    {
        var (locator, finds) = Finding(Gone);

        await Assert.ThrowsAsync<Win32Exception>(() => Runner(locator).RunAsync(["version"], Ct));

        Assert.Equal(2, finds());
    }

    [Fact]
    public async Task Without_a_locator_git_is_not_looked_for_again()
    {
        var runner = new GitRunner(_temp.Path, Gone.Apply(new GitRunnerOptions()));

        await Assert.ThrowsAsync<Win32Exception>(() => runner.RunAsync(["version"], Ct));
    }

    [Fact]
    public void The_options_name_the_git_the_locator_found_over_the_executable()
    {
        var options = new GitRunnerOptions { GitExecutable = "other-git", Locator = new GitLocator(() => Gone) };

        Assert.Equal(Gone, options.Installation);
        Assert.Equal(new GitInstallation("other-git", null), (options with { Locator = null }).Installation);
    }
}
