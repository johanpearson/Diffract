using System.ComponentModel;
using System.Globalization;
using Diffract.Git;

namespace Diffract.Sidecar;

internal enum GitUpdateStatus { UpToDate, UpdateAvailable, Unknown }

/// <summary>The result of <c>git.checkUpdate</c>.</summary>
/// <param name="Status">UpdateAvailable only when winget can install the update over the git Diffract uses.</param>
/// <param name="InstalledVersion">The git Diffract uses, for example "2.47.1". Null when neither git nor winget knows it.</param>
/// <param name="AvailableVersion">winget's newest Git for Windows, for example "2.55.0.5". Null when there is none or it is unknown.</param>
/// <param name="Problem">Why Diffract cannot tell or cannot update, in plain English. Null when it can.</param>
/// <param name="DownloadPage">Where to update by hand when winget cannot.</param>
internal sealed record GitUpdateCheck(GitUpdateStatus Status, string? InstalledVersion, string? AvailableVersion, string? Problem, string DownloadPage);

/// <summary>
/// Asks winget whether a newer Git for Windows is available. winget has no machine-readable output for this, so
/// the answer comes from its exit code and from the one table row with the package id. See docs/findings/winget.md.
/// Only ever runs <c>winget list</c>, which installs nothing.
/// </summary>
internal sealed class GitUpdateChecker(ICommandRunner commands, MachineLayout machine)
{
    public const string PackageId = "Git.Git";
    public const string DownloadPage = "https://gitforwindows.org/";

    /// <summary>winget refreshes its package index first when it is old, which can take a while on a slow network.</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Only the community source, so the Microsoft Store source and its agreement never get involved. Without a
    /// source column, the row is: name, id, installed version, and the available version when there is one.
    /// </summary>
    public static IReadOnlyList<string> ListArguments { get; } =
        ["list", "--id", PackageId, "--exact", "--source", "winget", "--disable-interactivity"];

    /// <summary>winget is an app execution alias in the user's WindowsApps folder, like Windows Terminal.</summary>
    public string? FindWinget()
    {
        var path = Path.Combine(machine.LocalAppData, "Microsoft", "WindowsApps", "winget.exe");
        return machine.FileExists(path) ? path : null;
    }

    /// <param name="installed">The git Diffract uses, or null when it could not be run.</param>
    public async Task<GitUpdateCheck> CheckAsync(GitVersion? installed, CancellationToken ct)
    {
        if (FindWinget() is not { } winget)
            return Unknown(installed, null, "winget is not installed, so Diffract cannot check for a newer Git for Windows.");

        try
        {
            return Decide(installed, await commands.RunAsync(winget, ListArguments, Timeout, ct).ConfigureAwait(false));
        }
        catch (TimeoutException)
        {
            return Unknown(installed, null, $"winget did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (Win32Exception e)
        {
            return Unknown(installed, null, $"winget could not be started: {e.Message}");
        }
    }

    /// <summary>What winget's answer means. Anything unexpected is Unknown with a reason, never a guess.</summary>
    internal static GitUpdateCheck Decide(GitVersion? installed, CommandResult result)
    {
        if (ExitProblem(result.ExitCode) is { } problem)
            return Unknown(installed, null, problem);

        if (WingetList.Find(result.StandardOutput, PackageId) is not { } row)
            return Unknown(installed, null, "winget's answer could not be read.");

        return Decide(installed, row);
    }

    private static string? ExitProblem(int exitCode) => exitCode switch
    {
        0 => null,
        WingetList.NoPackageFound =>
            "winget does not manage the Git for Windows on this machine, so Diffract cannot update it. It may have been installed another way.",
        WingetList.SourceAgreementsNotAccepted =>
            "winget needs its source agreement accepted first. Run 'winget list' once in a terminal and answer yes.",
        _ => $"winget failed with exit code 0x{exitCode:X8}.",
    };

    private static GitUpdateCheck Decide(GitVersion? installed, WingetListRow row)
    {
        var available = row.Available is null ? null : PackageVersion.Parse(row.Available);
        if (PackageVersion.TryParse(row.Version) is not { } managed)
            return Unknown(installed, row.Available, "winget does not know which version of Git for Windows is installed.");

        if (installed is not null && !managed.IsRelease(installed))
        {
            return Unknown(installed, row.Available,
                $"The git Diffract uses ({installed}) is not the Git for Windows that winget manages ({row.Version}), so an update through winget would not change it.");
        }

        var version = installed?.ToString() ?? row.Version;
        return available is not null && available.CompareTo(managed) > 0
            ? new GitUpdateCheck(GitUpdateStatus.UpdateAvailable, version, row.Available, null, DownloadPage)
            : new GitUpdateCheck(GitUpdateStatus.UpToDate, version, null, null, DownloadPage);
    }

    private static GitUpdateCheck Unknown(GitVersion? installed, string? available, string problem) =>
        new(GitUpdateStatus.Unknown, installed?.ToString(), available, problem, DownloadPage);
}

/// <param name="Version">As winget shows it. Not always a version: winget shows "Unknown" when it cannot tell.</param>
/// <param name="Available">Null when winget shows no newer version.</param>
internal sealed record WingetListRow(string Id, string Version, string? Available);

/// <summary>Reads the table <c>winget list --source winget</c> prints, as captured in the sidecar tests' Samples folder.</summary>
internal static class WingetList
{
    /// <summary>APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND: no installed package matches.</summary>
    public const int NoPackageFound = unchecked((int)0x8A150014);

    /// <summary>APPINSTALLER_CLI_ERROR_SOURCE_AGREEMENTS_NOT_ACCEPTED.</summary>
    public const int SourceAgreementsNotAccepted = unchecked((int)0x8A150046);

    /// <summary>
    /// The first row with this exact id and the expected columns, or null when there is none. The column headers are
    /// translated into the Windows display language and the name can hold spaces, so the row is found by its id, and
    /// the versions are the words after it. Versions never hold spaces. The id is the last word that matches, since
    /// the name comes before it and may hold the same word.
    /// </summary>
    public static WingetListRow? Find(string output, string id)
    {
        foreach (var line in output.Split('\n'))
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var at = Array.FindLastIndex(words, w => string.Equals(w, id, StringComparison.OrdinalIgnoreCase));
            WingetListRow? row = (words.Length - at - 1) switch
            {
                _ when at < 0 => null,
                1 => new(words[at], words[at + 1], null),
                // A source column, from a list without --source, would land here and is refused: "winget" is not a version.
                2 when PackageVersion.TryParse(words[at + 2]) is not null => new(words[at], words[at + 1], words[at + 2]),
                _ => null,
            };
            if (row is not null)
                return row;
        }
        return null;
    }
}

/// <summary>
/// A dotted version as winget shows it. Git for Windows adds its own release number as a fourth part:
/// "2.55.0.5" is Git for Windows 2.55.0(5), which <c>git version</c> calls "2.55.0.windows.5".
/// </summary>
internal sealed class PackageVersion : IComparable<PackageVersion>
{
    private readonly int[] _parts;

    private PackageVersion(int[] parts) => _parts = parts;

    public static PackageVersion? TryParse(string? text)
    {
        var parts = (text ?? "").Split('.');
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return null;
        }
        return new PackageVersion(numbers);
    }

    public static PackageVersion Parse(string text) => TryParse(text) ?? throw new FormatException($"Not a version: '{text}'");

    /// <summary>Whether this is the same git release, ignoring the Git for Windows release number.</summary>
    public bool IsRelease(GitVersion git) => CompareTo(new PackageVersion([git.Major, git.Minor, git.Patch]), parts: 3) == 0;

    /// <summary>Missing parts count as 0, so "2.47.1" equals "2.47.1.0".</summary>
    public int CompareTo(PackageVersion? other) => other is null ? 1 : CompareTo(other, Math.Max(_parts.Length, other._parts.Length));

    private int CompareTo(PackageVersion other, int parts)
    {
        for (var i = 0; i < parts; i++)
        {
            var compared = Part(i).CompareTo(other.Part(i));
            if (compared != 0)
                return compared;
        }
        return 0;
    }

    private int Part(int i) => i < _parts.Length ? _parts[i] : 0;

    public override string ToString() => string.Join('.', _parts);
}
