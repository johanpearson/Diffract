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
    public async Task App_info_over_the_protocol_uses_camel_case()
    {
        var result = await Methods().Dispatch("app.info", null, TestContext.Current.CancellationToken);

        Assert.Equal(["sidecarVersion", "gitVersion", "gitSupported", "gitProblem"], result.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.True, result.GetProperty("gitSupported").ValueKind);
    }

    [Fact]
    public void Git_calls_are_read_only_even_when_asked_otherwise()
    {
        Assert.True(new SidecarMethods(new GitRunnerOptions { ReadOnly = false }).RunnerOptions.ReadOnly);
    }
}
