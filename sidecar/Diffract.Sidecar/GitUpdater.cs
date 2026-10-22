using System.ComponentModel;
using Diffract.Git;

namespace Diffract.Sidecar;

/// <param name="Version">The version the user confirmed in the dialog, from <c>git.checkUpdate</c>'s availableVersion.</param>
internal sealed record GitUpdateParams(string? Version);

internal enum GitUpdateOutcome { Updated, RestartNeeded, Failed }

/// <summary>The result of <c>git.update</c>.</summary>
/// <param name="Message">What happened, in plain English.</param>
/// <param name="InUse">When git was in use and that stopped the update: the programs using it. Empty otherwise.</param>
internal sealed record GitUpdateResult(GitUpdateOutcome Outcome, string Message, IReadOnlyList<GitInUse> InUse);

/// <summary>
/// Updates Git for Windows through winget, to exactly the version the user confirmed. Plan, then apply: winget is
/// asked again first, and nothing is installed unless it still offers that version over the git Diffract uses.
/// </summary>
/// <param name="selfId">The sidecar's process id, so its own git calls never count as git in use.</param>
internal sealed class GitUpdater(GitUpdateChecker checker, ICommandRunner commands, IProcessTable processes, MachineLayout machine, int? selfId = null)
{
    private int _running;

    /// <summary>While true, Diffract starts no git of its own: the installer refuses to replace git while it runs.</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// The installer runs to the end once started, so Diffract never stops it: killing an installer halfway could
    /// leave git broken. If Diffract closes meanwhile, winget should carry on by itself.
    /// </summary>
    public static IReadOnlyList<string> UpgradeArguments(string version) =>
    [
        "upgrade", "--id", GitUpdateChecker.PackageId, "--exact", "--version", version, "--source", "winget",
        // winget cannot ask, so the licence is accepted here. The confirm dialog says so.
        "--disable-interactivity", "--accept-package-agreements",
    ];

    /// <summary>The programs using Git for Windows right now, apart from Diffract. Only reads the process list.</summary>
    public IReadOnlyList<GitInUse> InUse() =>
        GitForWindows.FindFolder(machine) is { } folder ? GitForWindows.Users(processes, folder, selfId ?? Environment.ProcessId) : [];

    /// <param name="installed">The git Diffract uses, or null when it could not be run.</param>
    /// <param name="ct">Cancels only the check before the update. Once winget upgrade starts, it runs to the end.</param>
    public async Task<GitUpdateResult> UpdateAsync(GitVersion? installed, string version, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return Failed("An update of Git for Windows is already running.");
        try
        {
            var check = await checker.CheckAsync(installed, ct).ConfigureAwait(false);
            if (check.Status != GitUpdateStatus.UpdateAvailable)
                return Failed($"Nothing was installed. {check.Problem ?? "winget no longer offers a newer Git for Windows."}");
            if (check.AvailableVersion != version)
                return Failed($"Nothing was installed. winget now offers {check.AvailableVersion}, not the {version} you confirmed. Check again.");

            CommandResult result;
            try
            {
                // No progress to show: winget prints none when its output is redirected, and the installer shows
                // its own window. See docs/findings/winget.md.
                result = await commands.RunAsync(checker.FindWinget()!, UpgradeArguments(version), Timeout.InfiniteTimeSpan, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Win32Exception e)
            {
                return Failed($"winget could not be started: {e.Message}");
            }
            return Outcome(result.ExitCode, version, InUse);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>What winget's exit code means for the user. The codes are listed in docs/findings/winget.md.</summary>
    /// <param name="inUse">Asked only when the installer stopped, to name the programs that were using git.</param>
    internal static GitUpdateResult Outcome(int exitCode, string version, Func<IReadOnlyList<GitInUse>> inUse) => (uint)exitCode switch
    {
        0 => new(GitUpdateOutcome.Updated, $"Git for Windows {version} is installed.", []),
        0x8A150109 or 0x8A15010A or 0x8A15010B => new(GitUpdateOutcome.RestartNeeded, $"Git for Windows {version} is installed. Restart Windows to finish the update.", []),
        // 0x8A150006: the installer itself ended with an error. Git for Windows' installer does that when programs use
        // git, because winget runs it silently and it cannot ask for them to be closed.
        0x8A150006 or 0x8A150101 or 0x8A150103 or 0x8A150111 => InstallerStopped(exitCode, inUse()),
        _ => OtherFailure(exitCode),
    };

    private static GitUpdateResult OtherFailure(int exitCode) => (uint)exitCode switch
    {
        0x8A15010C => Failed("The update was cancelled."),
        0x8A15002B => Failed("winget found no update to install."),
        0x8A150008 or 0x8A150107 or 0x8A15002E or 0x8A150086 => Failed("Downloading the update failed. Check the network connection and try again."),
        _ => Failed($"The update failed: winget ended with exit code 0x{exitCode:X8}."),
    };

    private static GitUpdateResult InstallerStopped(int exitCode, IReadOnlyList<GitInUse> inUse) => inUse.Count > 0
        ? new(GitUpdateOutcome.Failed, "Git for Windows was not updated, because these programs are using git. Close them, then try again.", inUse)
        : exitCode == unchecked((int)0x8A150006)
            ? Failed("The Git for Windows installer stopped before it finished. If a program was using git, close it and try again.")
            : Failed("Git was in use while the installer ran. Close the programs that use git, then try again.");

    private static GitUpdateResult Failed(string message) => new(GitUpdateOutcome.Failed, message, []);
}
