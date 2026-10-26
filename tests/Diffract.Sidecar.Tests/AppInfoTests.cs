using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;

namespace Diffract.Sidecar.Tests;

public sealed class AppInfoTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private SidecarMethods Methods(string? gitExecutable = null) => new(new GitRunnerOptions
    {
        Environment = IsolatedGit.Environment(_temp),
        GitExecutable = gitExecutable ?? "git",
    });

    [Fact]
    public async Task Reports_its_own_version_and_a_supported_git()
    {
        var info = await Methods().AppInfoAsync(TestContext.Current.CancellationToken);

        Assert.Matches(@"^\d+\.\d+\.\d+$", info.SidecarVersion);
        Assert.Matches(@"^\d+\.\d+\.\d+$", info.GitVersion);
        Assert.True(info.GitSupported);
        Assert.Null(info.GitProblem);
    }

    [Fact]
    public async Task Old_git_is_reported_with_the_reason()
    {
        var fakeGit = _temp.Combine("old-git.cmd");
        File.WriteAllText(fakeGit, "@echo git version 2.39.5.windows.1\r\n");

        var info = await Methods(fakeGit).AppInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal(("2.39.5", false), (info.GitVersion, info.GitSupported));
        Assert.Contains("2.40.0", info.GitProblem);
    }

    [Fact]
    public async Task Missing_git_is_reported_not_thrown()
    {
        var info = await Methods(_temp.Combine("no-such-git.exe")).AppInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal((null, false), (info.GitVersion, info.GitSupported));
        Assert.StartsWith("Git was not found", info.GitProblem);
    }

    [Fact]
    public async Task Git_that_moved_since_the_start_is_found_again()
    {
        // As after Git for Windows 2.56, which moved the real git from mingw64 to ucrt64 while Diffract ran.
        var gone = new GitInstallation(_temp.Combine("mingw64", "bin", "git.exe"), null);
        var finds = 0;
        var locator = new GitLocator(() => finds++ == 0 ? gone : GitInstallation.ForThisMachine());
        var methods = new SidecarMethods(new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp), Locator = locator });

        var info = await methods.AppInfoAsync(TestContext.Current.CancellationToken);

        Assert.True(info.GitSupported, info.GitProblem);
        Assert.Equal(GitInstallation.ForThisMachine(), locator.Current);
    }

    [Fact]
    public async Task Git_that_is_there_but_cannot_start_is_not_called_missing()
    {
        var notAProgram = _temp.Combine("git.exe");
        File.WriteAllText(notAProgram, "not a program");

        var info = await Methods(notAProgram).AppInfoAsync(TestContext.Current.CancellationToken);

        Assert.False(info.GitSupported);
        Assert.StartsWith($"Git could not be started ({notAProgram}): ", info.GitProblem);
    }

    [Fact]
    public async Task App_info_over_the_protocol_uses_camel_case()
    {
        var result = await Methods().Dispatch("app.info", null, TestContext.Current.CancellationToken);

        Assert.Equal(["sidecarVersion", "gitVersion", "gitSupported", "gitProblem"], result.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.True, result.GetProperty("gitSupported").ValueKind);
    }
}
