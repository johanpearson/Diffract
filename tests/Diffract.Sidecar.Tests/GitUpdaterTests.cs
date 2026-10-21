using System.Text;
using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// git.update and git.openDownloadPage. winget is always a fake: no test installs anything, and none opens a browser.
/// </summary>
public sealed class GitUpdaterTests : IDisposable
{
    private const string Winget = @"C:\Users\johan\AppData\Local\Microsoft\WindowsApps\winget.exe";

    private static readonly GitVersion Git2471 = new(2, 47, 1);

    private static readonly MachineLayout Machine =
        new(@"C:\Windows\System32", @"C:\Users\johan\AppData\Local", @"C:\Program Files", null, f => f == Winget);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string Sample(string name) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Samples", name)));

    /// <summary>winget offers 2.55.0.5 over 2.47.1, as on Johan's laptop, and the upgrade ends with this exit code.</summary>
    private static FakeCommands Answering(int upgradeExitCode = 0, string list = "winget-list-git-update-available.txt") =>
        new((_, args) => args[0] == "list" ? new CommandResult(0, Sample(list)) : new CommandResult(upgradeExitCode, ""));

    private static GitUpdater Updater(FakeCommands commands) => new(new GitUpdateChecker(commands, Machine), commands);

    private static IEnumerable<string> Subcommands(FakeCommands commands) => commands.Ran.Select(r => r.Arguments[0]);

    [Fact]
    public async Task Installs_exactly_the_confirmed_version_after_checking_again()
    {
        var commands = Answering();

        var result = await Updater(commands).UpdateAsync(Git2471, "2.55.0.5", TestContext.Current.CancellationToken);

        Assert.Equal(new GitUpdateResult(GitUpdateOutcome.Updated, "Git for Windows 2.55.0.5 is installed."), result);
        Assert.Equal(["list", "upgrade"], Subcommands(commands));
        var (fileName, arguments, _) = commands.Ran[1];
        Assert.Equal(Winget, fileName);
        Assert.Equal(
            ["upgrade", "--id", "Git.Git", "--exact", "--version", "2.55.0.5", "--source", "winget", "--disable-interactivity", "--accept-package-agreements"],
            arguments);
    }

    [Fact]
    public async Task The_installer_is_never_stopped_halfway()
    {
        var commands = Answering();

        await Updater(commands).UpdateAsync(Git2471, "2.55.0.5", TestContext.Current.CancellationToken);

        Assert.Equal(Timeout.InfiniteTimeSpan, commands.Ran[1].Timeout);
        Assert.False(commands.Tokens[1].CanBeCanceled);
    }

    [Fact]
    public async Task Installs_nothing_when_winget_now_offers_another_version()
    {
        var commands = Answering();

        var result = await Updater(commands).UpdateAsync(Git2471, "2.54.0.1", TestContext.Current.CancellationToken);

        Assert.Equal(["list"], Subcommands(commands));
        Assert.Equal(GitUpdateOutcome.Failed, result.Outcome);
        Assert.Equal("Nothing was installed. winget now offers 2.55.0.5, not the 2.54.0.1 you confirmed. Check again.", result.Message);
    }

    [Fact]
    public async Task Installs_nothing_when_another_git_is_the_one_Diffract_uses()
    {
        var commands = Answering();

        var result = await Updater(commands).UpdateAsync(new GitVersion(2, 43, 0), "2.55.0.5", TestContext.Current.CancellationToken);

        Assert.Equal(["list"], Subcommands(commands));
        Assert.StartsWith("Nothing was installed. The git Diffract uses (2.43.0)", result.Message);
    }

    [Fact]
    public async Task Installs_nothing_when_winget_does_not_manage_git()
    {
        var commands = new FakeCommands((_, _) => new CommandResult(WingetList.NoPackageFound, Sample("winget-list-not-found.txt")));

        var result = await Updater(commands).UpdateAsync(Git2471, "2.55.0.5", TestContext.Current.CancellationToken);

        Assert.Equal(["list"], Subcommands(commands));
        Assert.Equal(GitUpdateOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task Only_one_update_runs_at_a_time()
    {
        var upgrade = new TaskCompletionSource<CommandResult>();
        var commands = FakeCommands.Async((_, args) => args[0] == "list"
            ? Task.FromResult(new CommandResult(0, Sample("winget-list-git-update-available.txt")))
            : upgrade.Task);
        var updater = Updater(commands);

        var first = updater.UpdateAsync(Git2471, "2.55.0.5", TestContext.Current.CancellationToken);
        var second = await updater.UpdateAsync(Git2471, "2.55.0.5", TestContext.Current.CancellationToken);
        upgrade.SetResult(new CommandResult(0, ""));

        Assert.Equal(new GitUpdateResult(GitUpdateOutcome.Failed, "An update of Git for Windows is already running."), second);
        Assert.Equal(GitUpdateOutcome.Updated, (await first).Outcome);
        Assert.Equal(1, Subcommands(commands).Count(s => s == "upgrade"));
        Assert.Equal(GitUpdateOutcome.Updated, (await updater.UpdateAsync(Git2471, "2.55.0.5", TestContext.Current.CancellationToken)).Outcome);
    }

    [Theory]
    [InlineData(0, "Updated", "Git for Windows 2.55.0.5 is installed.")]
    [InlineData(unchecked((int)0x8A150109), "RestartNeeded", "Restart Windows")]
    [InlineData(unchecked((int)0x8A15010B), "RestartNeeded", "Restart Windows")]
    [InlineData(unchecked((int)0x8A150101), "Failed", "Git is in use")]
    [InlineData(unchecked((int)0x8A150103), "Failed", "Git is in use")]
    [InlineData(unchecked((int)0x8A150111), "Failed", "Git is in use")]
    [InlineData(unchecked((int)0x8A15010C), "Failed", "cancelled")]
    [InlineData(unchecked((int)0x8A15002B), "Failed", "no update to install")]
    [InlineData(unchecked((int)0x8A150008), "Failed", "network")]
    [InlineData(unchecked((int)0x8A150107), "Failed", "network")]
    [InlineData(1, "Failed", "exit code 0x00000001")]
    public void Each_exit_code_is_explained(int exitCode, string outcome, string expected)
    {
        var result = GitUpdater.Outcome(exitCode, "2.55.0.5");

        Assert.Equal(outcome, result.Outcome.ToString());
        Assert.Contains(expected, result.Message);
    }

    // --- through the protocol -----------------------------------------------------

    private SidecarMethods Methods(FakeCommands commands, FakeProcesses? processes = null)
    {
        var fakeGit = _temp.Combine("git.cmd");
        File.WriteAllText(fakeGit, "@echo git version 2.47.1.windows.1\r\n");
        return new SidecarMethods(
            new GitRunnerOptions { GitExecutable = fakeGit, Environment = IsolatedGit.Environment(_temp) },
            machine: Machine,
            commands: commands,
            processes: processes);
    }

    [Fact]
    public async Task The_method_updates_to_the_version_in_its_params()
    {
        var commands = Answering(upgradeExitCode: unchecked((int)0x8A150109));
        using var methods = Methods(commands);

        var result = await methods.Dispatch("git.update", JsonSerializer.SerializeToElement(new { version = "2.55.0.5" }), TestContext.Current.CancellationToken);

        Assert.Equal(
            """{"outcome":"RestartNeeded","message":"Git for Windows 2.55.0.5 is installed. Restart Windows to finish the update."}""",
            JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task The_method_needs_the_confirmed_version()
    {
        var commands = Answering();
        using var methods = Methods(commands);

        var e = await Assert.ThrowsAsync<RpcException>(() =>
            methods.Dispatch("git.update", JsonSerializer.SerializeToElement(new { }), TestContext.Current.CancellationToken));

        Assert.Equal((RpcErrorCodes.InvalidParams, "'version' is required"), (e.Code, e.Message));
        Assert.Empty(commands.Ran);
    }

    [Fact]
    public async Task The_download_page_opens_in_the_browser_through_Explorer()
    {
        var processes = new FakeProcesses();
        using var methods = Methods(Answering(), processes);

        await methods.Dispatch("git.openDownloadPage", null, TestContext.Current.CancellationToken);

        var started = Assert.Single(processes.Started);
        Assert.EndsWith(@"\explorer.exe", started.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["https://gitforwindows.org/"], started.Arguments);
    }
}
