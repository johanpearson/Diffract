using System.ComponentModel;
using Diffract.Git;

namespace Diffract.Sidecar;

/// <param name="Version">The version the user confirmed in the dialog, from <c>git.checkUpdate</c>'s availableVersion.</param>
internal sealed record GitUpdateParams(string? Version);

internal enum GitUpdateOutcome { Updated, RestartNeeded, Failed }

/// <summary>The result of <c>git.update</c>.</summary>
/// <param name="Message">What happened, in plain English.</param>
internal sealed record GitUpdateResult(GitUpdateOutcome Outcome, string Message);

/// <summary>
/// Updates Git for Windows through winget, to exactly the version the user confirmed. Plan, then apply: winget is
/// asked again first, and nothing is installed unless it still offers that version over the git Diffract uses.
/// </summary>
internal sealed class GitUpdater(GitUpdateChecker checker, ICommandRunner commands)
{
    private int _running;

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
                result = await commands.RunAsync(checker.FindWinget()!, UpgradeArguments(version), System.Threading.Timeout.InfiniteTimeSpan, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Win32Exception e)
            {
                return Failed($"winget could not be started: {e.Message}");
            }
            return Outcome(result.ExitCode, version);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>What winget's exit code means for the user. The codes are listed in docs/findings/winget.md.</summary>
    internal static GitUpdateResult Outcome(int exitCode, string version) => (uint)exitCode switch
    {
        0 => new(GitUpdateOutcome.Updated, $"Git for Windows {version} is installed."),
        0x8A150109 or 0x8A15010A or 0x8A15010B => new(GitUpdateOutcome.RestartNeeded, $"Git for Windows {version} is installed. Restart Windows to finish the update."),
        0x8A150101 or 0x8A150103 or 0x8A150111 =>
            Failed("Git is in use, for example by an agent, Git Bash or an editor. Close those programs and try again."),
        0x8A15010C => Failed("The update was cancelled."),
        0x8A15002B => Failed("winget found no update to install."),
        0x8A150008 or 0x8A150107 or 0x8A15002E or 0x8A150086 => Failed("Downloading the update failed. Check the network connection and try again."),
        _ => Failed($"The update failed: winget ended with exit code 0x{exitCode:X8}. If Windows asked for permission and was refused, that is the reason."),
    };

    private static GitUpdateResult Failed(string message) => new(GitUpdateOutcome.Failed, message);
}
