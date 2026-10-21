using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// git.checkUpdate: reading winget's table, comparing versions and deciding whether to offer an update, against
/// winget output captured in Samples (see capture-winget-samples.ps1). winget itself never runs in a test.
/// </summary>
public sealed class GitUpdateCheckTests : IDisposable
{
    private const string Winget = @"C:\Users\johan\AppData\Local\Microsoft\WindowsApps\winget.exe";

    private static readonly GitVersion Git2471 = new(2, 47, 1);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string Sample(string name) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Samples", name)));

    private static MachineLayout Machine(bool wingetInstalled = true) =>
        new(@"C:\Windows\System32", @"C:\Users\johan\AppData\Local", @"C:\Program Files", null, f => wingetInstalled && f == Winget);

    private static CommandResult Ok(string output) => new(0, output);

    /// <summary>The shape of winget-list-up-to-date.txt, for Git for Windows: no available column.</summary>
    private const string GitUpToDate = """
        Name Id      Version
        --------------------
        Git  Git.Git 2.55.0.5

        """;

    // --- reading the table --------------------------------------------------------

    [Fact]
    public void Reads_the_installed_and_available_version()
    {
        var row = WingetList.Find(Sample("winget-list-git-update-available.txt"), "Git.Git");

        Assert.Equal(new WingetListRow("Git.Git", "2.47.1", "2.55.0.5"), row);
    }

    [Fact]
    public void A_package_with_no_newer_version_has_no_available_version()
    {
        var row = WingetList.Find(Sample("winget-list-up-to-date.txt"), "GitExtensionsTeam.GitExtensions");

        Assert.Equal(new WingetListRow("GitExtensionsTeam.GitExtensions", "7.2.1.7", null), row);
    }

    [Fact]
    public void An_unknown_installed_version_is_kept_as_winget_shows_it()
    {
        var row = WingetList.Find(Sample("winget-list-unknown-version.txt"), "KDE.KDiff3");

        Assert.Equal(new WingetListRow("KDE.KDiff3", "Unknown", "1.12.4"), row);
    }

    [Fact]
    public void Not_found_output_has_no_row()
    {
        Assert.Null(WingetList.Find(Sample("winget-list-not-found.txt"), "Git.Git"));
    }

    [Theory]
    [InlineData("Git.Git", "2.47.1", "2.55.0.5")]
    [InlineData("GitExtensionsTeam.GitExtensions", "7.2.1.7", null)]
    [InlineData("KDE.KDiff3", "Unknown", "1.12.4")]
    [InlineData("Microsoft.DotNet.Native.Runtime", "2.2.28604.0", null)]
    [InlineData("Microsoft.DotNet.SDK.8", "8.0.206", "8.0.426")]
    public void Finds_the_right_row_among_many_whatever_the_name(string id, string version, string? available)
    {
        // Names with spaces, versions in them and brackets, and a package listed twice.
        Assert.Equal(new WingetListRow(id, version, available), WingetList.Find(Sample("winget-list-many.txt"), id));
    }

    [Fact]
    public void Only_an_exact_id_matches()
    {
        Assert.Null(WingetList.Find(Sample("winget-list-many.txt"), "Git"));
        Assert.Null(WingetList.Find(Sample("winget-list-many.txt"), "GitHub"));
        Assert.Null(WingetList.Find(Sample("winget-list-many.txt"), "Git.Gi"));
    }

    [Fact]
    public void A_name_that_holds_the_id_does_not_hide_the_row()
    {
        var output = "Name Id Version Available\n---\nGit.Git helper  Some.Helper 1.0\nGit.Git  Git.Git 2.47.1  2.55.0.5\n";

        Assert.Equal(new WingetListRow("Git.Git", "2.47.1", "2.55.0.5"), WingetList.Find(output, "Git.Git"));
    }

    [Fact]
    public void Reads_the_line_endings_winget_writes()
    {
        var crlf = Sample("winget-list-git-update-available.txt").ReplaceLineEndings("\r\n");

        Assert.Equal(new WingetListRow("Git.Git", "2.47.1", "2.55.0.5"), WingetList.Find(crlf, "Git.Git"));
    }

    [Theory]
    [InlineData("Git  Git.Git 2.47.1  winget")]             // a source column, as without --source
    [InlineData("Git  Git.Git 2.47.1  2.55.0.5 winget")]    // an extra column
    [InlineData("Git  Git.Git")]                            // no version
    public void A_row_of_another_shape_is_refused(string line)
    {
        Assert.Null(WingetList.Find($"Name Id Version\n---\n{line}\n", "Git.Git"));
    }

    // --- versions -----------------------------------------------------------------

    [Theory]
    [InlineData("2.55.0.5", "2.47.1", 1)]
    [InlineData("2.55.0.5", "2.55.0.4", 1)]
    [InlineData("2.47.10", "2.47.9", 1)]
    [InlineData("2.47.1", "2.47.1.0", 0)]
    [InlineData("2.47.1", "2.47.1", 0)]
    [InlineData("2.9", "2.10", -1)]
    public void Versions_compare_part_by_part(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(PackageVersion.Parse(left).CompareTo(PackageVersion.Parse(right))));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData(">")]
    [InlineData("2.x")]
    [InlineData("-1")]
    [InlineData("2..1")]
    public void Anything_but_a_dotted_number_is_not_a_version(string text)
    {
        Assert.Null(PackageVersion.TryParse(text));
    }

    [Theory]
    [InlineData("2.47.1", true)]
    [InlineData("2.47.1.1", true)]
    [InlineData("2.47.1.4", true)]
    [InlineData("2.47.2", false)]
    [InlineData("2.48.1", false)]
    public void The_git_release_ignores_the_Git_for_Windows_release_number(string winget, bool same)
    {
        Assert.Equal(same, PackageVersion.Parse(winget).IsRelease(Git2471));
    }

    // --- deciding -----------------------------------------------------------------

    [Fact]
    public void Offers_a_newer_Git_for_Windows()
    {
        var check = GitUpdateChecker.Decide(Git2471, Ok(Sample("winget-list-git-update-available.txt")));

        Assert.Equal(new GitUpdateCheck(GitUpdateStatus.UpdateAvailable, "2.47.1", "2.55.0.5", null, "https://gitforwindows.org/"), check);
    }

    [Fact]
    public void Nothing_to_offer_when_winget_has_no_newer_version()
    {
        var check = GitUpdateChecker.Decide(new GitVersion(2, 55, 0), Ok(GitUpToDate));

        Assert.Equal((GitUpdateStatus.UpToDate, "2.55.0", (string?)null, (string?)null), (check.Status, check.InstalledVersion, check.AvailableVersion, check.Problem));
    }

    [Fact]
    public void Does_not_offer_an_update_when_another_git_is_the_one_Diffract_uses()
    {
        // winget would update the Git for Windows it manages, but git on PATH is a different one.
        var check = GitUpdateChecker.Decide(new GitVersion(2, 43, 0), Ok(Sample("winget-list-git-update-available.txt")));

        Assert.Equal((GitUpdateStatus.Unknown, "2.43.0", "2.55.0.5"), (check.Status, check.InstalledVersion, check.AvailableVersion));
        Assert.Contains("(2.43.0)", check.Problem);
        Assert.Contains("(2.47.1)", check.Problem);
    }

    [Fact]
    public void Uses_wingets_version_when_git_could_not_run()
    {
        var check = GitUpdateChecker.Decide(null, Ok(Sample("winget-list-git-update-available.txt")));

        Assert.Equal((GitUpdateStatus.UpdateAvailable, "2.47.1", "2.55.0.5"), (check.Status, check.InstalledVersion, check.AvailableVersion));
    }

    [Fact]
    public void Git_for_Windows_that_winget_does_not_manage_cannot_be_updated()
    {
        var check = GitUpdateChecker.Decide(Git2471, new CommandResult(WingetList.NoPackageFound, Sample("winget-list-not-found.txt")));

        Assert.Equal((GitUpdateStatus.Unknown, "2.47.1", (string?)null), (check.Status, check.InstalledVersion, check.AvailableVersion));
        Assert.Contains("does not manage", check.Problem);
        Assert.Equal("https://gitforwindows.org/", check.DownloadPage);
    }

    [Fact]
    public void An_unknown_installed_version_is_not_guessed()
    {
        var output = Sample("winget-list-unknown-version.txt").Replace("KDE.KDiff3", "Git.Git", StringComparison.Ordinal);

        var check = GitUpdateChecker.Decide(Git2471, Ok(output));

        Assert.Equal((GitUpdateStatus.Unknown, "1.12.4"), (check.Status, check.AvailableVersion));
        Assert.Contains("does not know which version", check.Problem);
    }

    [Theory]
    [InlineData(unchecked((int)0x8A150046), "source agreement")]
    [InlineData(unchecked((int)0x8A15000F), "exit code 0x8A15000F")]
    [InlineData(1, "exit code 0x00000001")]
    public void A_winget_error_is_explained(int exitCode, string expected)
    {
        var check = GitUpdateChecker.Decide(Git2471, new CommandResult(exitCode, ""));

        Assert.Equal(GitUpdateStatus.Unknown, check.Status);
        Assert.Contains(expected, check.Problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Name Id Version\n---\nGit  Git.Git 2.47.1  winget\n")]
    [InlineData("Name Id Version\n---\nGit for Windows  Git.Git.Portable 2.47.1\n")]
    public void Output_that_cannot_be_read_is_unknown(string output)
    {
        var check = GitUpdateChecker.Decide(Git2471, Ok(output));

        Assert.Equal((GitUpdateStatus.Unknown, "winget's answer could not be read."), (check.Status, check.Problem));
    }

    // --- running winget -----------------------------------------------------------

    [Fact]
    public async Task Runs_only_winget_list_for_Git_for_Windows()
    {
        var commands = new FakeCommands((_, _) => Ok(Sample("winget-list-git-update-available.txt")));

        await new GitUpdateChecker(commands, Machine()).CheckAsync(Git2471, TestContext.Current.CancellationToken);

        var (fileName, arguments, timeout) = Assert.Single(commands.Ran);
        Assert.Equal(Winget, fileName);
        Assert.Equal(["list", "--id", "Git.Git", "--exact", "--source", "winget", "--disable-interactivity"], arguments);
        Assert.Equal(TimeSpan.FromSeconds(60), timeout);
    }

    [Fact]
    public async Task Without_winget_nothing_runs()
    {
        var commands = new FakeCommands((_, _) => throw new InvalidOperationException("must not run"));

        var check = await new GitUpdateChecker(commands, Machine(wingetInstalled: false)).CheckAsync(Git2471, TestContext.Current.CancellationToken);

        Assert.Empty(commands.Ran);
        Assert.Equal((GitUpdateStatus.Unknown, "2.47.1"), (check.Status, check.InstalledVersion));
        Assert.Contains("winget is not installed", check.Problem);
    }

    [Fact]
    public async Task A_winget_that_does_not_answer_in_time_is_unknown()
    {
        var commands = new FakeCommands((_, _) => throw new TimeoutException());

        var check = await new GitUpdateChecker(commands, Machine()).CheckAsync(Git2471, TestContext.Current.CancellationToken);

        Assert.Equal((GitUpdateStatus.Unknown, "winget did not answer within 60 seconds."), (check.Status, check.Problem));
    }

    [Fact]
    public async Task A_winget_that_cannot_start_is_unknown()
    {
        var commands = new FakeCommands((_, _) => throw new Win32Exception(2));

        var check = await new GitUpdateChecker(commands, Machine()).CheckAsync(Git2471, TestContext.Current.CancellationToken);

        Assert.Equal(GitUpdateStatus.Unknown, check.Status);
        Assert.StartsWith("winget could not be started", check.Problem);
    }

    [Fact]
    public async Task The_method_answers_with_the_git_Diffract_uses_and_wingets_newest()
    {
        var fakeGit = _temp.Combine("git.cmd");
        File.WriteAllText(fakeGit, "@echo git version 2.47.1.windows.1\r\n");
        var commands = new FakeCommands((_, _) => Ok(Sample("winget-list-git-update-available.txt")));
        using var methods = new SidecarMethods(
            new GitRunnerOptions { GitExecutable = fakeGit, Environment = IsolatedGit.Environment(_temp) },
            machine: Machine(),
            commands: commands);

        var result = await methods.Dispatch("git.checkUpdate", null, TestContext.Current.CancellationToken);

        Assert.Equal(
            """{"status":"UpdateAvailable","installedVersion":"2.47.1","availableVersion":"2.55.0.5","problem":null,"downloadPage":"https://gitforwindows.org/"}""",
            JsonSerializer.Serialize(result));
    }
}
