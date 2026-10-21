using Diffract.Fixtures;

namespace Diffract.Git.Tests;

/// <summary>Finding the real git behind Git for Windows' launcher, on described machines and on this one.</summary>
public sealed class GitInstallationTests : IDisposable
{
    private const string Git = @"C:\Program Files\Git";
    private const string RealGit = Git + @"\mingw64\bin\git.exe";
    private const string Prefix = Git + @"\mingw64\bin;" + Git + @"\usr\bin";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static GitInstallation Find(string path, params string[] files) =>
        GitInstallation.Find(path, f => files.Contains(f, StringComparer.OrdinalIgnoreCase));

    [Theory]
    [InlineData(Git + @"\cmd", Git + @"\cmd\git.exe")]
    [InlineData(Git + @"\bin", Git + @"\bin\git.exe")]
    [InlineData(Git + @"\mingw64\bin", RealGit)]
    [InlineData("\"" + Git + @"\cmd\" + "\"", Git + @"\cmd\git.exe")]
    public void Starts_the_real_git_behind_any_Git_for_Windows_entry_on_PATH(string entry, string onPath)
    {
        var found = Find(@"C:\Windows\system32;" + entry + @";C:\Tools", onPath, RealGit);

        Assert.Equal(new GitInstallation(RealGit, Prefix), found);
    }

    [Fact]
    public void Only_the_first_git_on_PATH_counts_as_Windows_would_pick_it()
    {
        // A package manager's shim comes first, so "git" means the shim, and Diffract leaves it alone.
        var found = Find(@"C:\scoop\shims;" + Git + @"\cmd", @"C:\scoop\shims\git.exe", Git + @"\cmd\git.exe", RealGit);

        Assert.Equal(GitInstallation.OnPath, found);
    }

    [Fact]
    public void A_launcher_without_the_real_git_next_to_it_is_left_alone()
    {
        Assert.Equal(GitInstallation.OnPath, Find(Git + @"\cmd", Git + @"\cmd\git.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Windows\system32;relative\folder")]
    public void No_git_on_PATH_starts_git_as_before(string? path)
    {
        Assert.Equal(GitInstallation.OnPath, GitInstallation.Find(path, _ => false));
    }

    [Fact]
    public void Options_carry_the_executable_and_the_path_prefix()
    {
        var options = new GitInstallation(RealGit, Prefix).Apply(new GitRunnerOptions { ReadOnly = false });

        Assert.Equal((RealGit, Prefix, false), (options.GitExecutable, options.PathPrefix, options.ReadOnly));
    }

    // --- on this machine ------------------------------------------------------------

    [Fact]
    public async Task On_this_machine_it_starts_a_git_that_answers_like_the_one_on_PATH()
    {
        var installation = GitInstallation.ForThisMachine();
        var direct = new GitRunner(_temp.Path, installation.Apply(new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) }));
        var launcher = new GitRunner(_temp.Path, new GitRunnerOptions { Environment = IsolatedGit.Environment(_temp) });

        var ct = TestContext.Current.CancellationToken;
        Assert.Equal((await launcher.RunAsync(["version"], ct)).StandardOutput, (await direct.RunAsync(["version"], ct)).StandardOutput);
        if (installation != GitInstallation.OnPath)
            Assert.True(File.Exists(installation.Executable));
    }

    [Fact]
    public async Task The_path_prefix_comes_before_the_rest_of_PATH()
    {
        // A one-off alias prints the PATH git hands its children. -c writes no config, and this runs in a temp folder.
        // git puts its own libexec folder first, so the prefix only has to come before the PATH Diffract was given.
        var folder = _temp.Combine("first-on-path");
        Directory.CreateDirectory(folder);
        var options = GitInstallation.ForThisMachine().Apply(new GitRunnerOptions { ReadOnly = false, Environment = IsolatedGit.Environment(_temp) });
        var runner = new GitRunner(_temp.Path, options with { PathPrefix = folder + (options.PathPrefix is null ? "" : Path.PathSeparator + options.PathPrefix) });

        var output = (await runner.RunAsync(["-c", "alias.showpath=!cmd //c echo %PATH%", "showpath"], TestContext.Current.CancellationToken)).StandardOutput;

        var at = output.IndexOf(folder, StringComparison.OrdinalIgnoreCase);
        var system = output.IndexOf(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.True(at >= 0 && system > at, output);
    }
}
