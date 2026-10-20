using Diffract.Fixtures;

namespace Diffract.Git.Tests;

public class GitVersionTests
{
    [Theory]
    [InlineData("git version 2.47.1.windows.1\n", 2, 47, 1)]
    [InlineData("git version 2.56.0.windows.2\n", 2, 56, 0)]
    [InlineData("git version 2.40.0", 2, 40, 0)]
    [InlineData("git version 2.43", 2, 43, 0)]
    [InlineData("git version 2.39.5 (Apple Git-154)", 2, 39, 5)]
    public void Parses_version_output(string output, int major, int minor, int patch)
    {
        Assert.Equal(new GitVersion(major, minor, patch), GitVersion.Parse(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not git")]
    [InlineData("git version two")]
    public void Rejects_other_output(string output)
    {
        Assert.Throws<FormatException>(() => GitVersion.Parse(output));
    }

    [Theory]
    [InlineData(2, 40, 0, true)]
    [InlineData(2, 47, 1, true)]
    [InlineData(3, 0, 0, true)]
    [InlineData(2, 39, 9, false)]
    [InlineData(1, 99, 0, false)]
    public void Supports_2_40_and_newer(int major, int minor, int patch, bool supported)
    {
        Assert.Equal(supported, new GitVersion(major, minor, patch).IsSupported);
    }

    [Fact]
    public async Task Git_on_this_machine_is_supported()
    {
        using var temp = new TempDirectory();

        var version = await GitVersion.RequireSupportedAsync(IsolatedGit.ReadOnlyRunner(temp, temp.Path), TestContext.Current.CancellationToken);

        Assert.True(version.IsSupported);
    }

    [Fact]
    public async Task Old_git_fails_with_a_clear_message()
    {
        using var temp = new TempDirectory();
        var fakeGit = temp.Combine("old-git.cmd");
        File.WriteAllText(fakeGit, "@echo git version 2.39.0.windows.1\r\n");
        var runner = new GitRunner(temp.Path, new GitRunnerOptions { GitExecutable = fakeGit });

        var ex = await Assert.ThrowsAsync<GitVersionTooOldException>(() => GitVersion.RequireSupportedAsync(runner, TestContext.Current.CancellationToken));

        Assert.Equal(new GitVersion(2, 39, 0), ex.Found);
        Assert.Contains("2.40.0 or newer", ex.Message);
        Assert.Contains("found 2.39.0", ex.Message);
    }
}
