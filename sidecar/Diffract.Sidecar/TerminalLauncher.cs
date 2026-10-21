namespace Diffract.Sidecar;

/// <summary>
/// Opens a terminal in a folder: Windows Terminal when it is installed, otherwise Windows PowerShell.
/// Never <c>cmd /c</c>, and never a command line built from strings.
/// </summary>
/// <param name="findWindowsTerminal">The path of wt.exe, or null when it is not installed. Tests pass their own.</param>
internal sealed class TerminalLauncher(IProcessStarter processes, Func<string?> findWindowsTerminal)
{
    public TerminalLauncher(IProcessStarter processes) : this(processes, FindWindowsTerminal)
    {
    }

    /// <param name="folder">An existing, fully qualified folder.</param>
    public void Open(string folder)
    {
        // Windows Terminal reads ";" as "start another command", even inside a quoted path, so such a folder
        // would open the wrong thing. PowerShell takes the folder as its working directory, which has no such rule.
        if (!folder.Contains(';') && findWindowsTerminal() is { } wt)
            processes.Start(wt, ["-d", folder]);
        else
            processes.Start(PowerShell, ["-NoLogo"], workingDirectory: folder, newConsole: true);
    }

    /// <summary>Windows PowerShell, which every Windows 11 has, by its full path so nothing on PATH can stand in for it.</summary>
    public static string PowerShell { get; } =
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Windows Terminal installs wt.exe as an app execution alias in the user's WindowsApps folder.</summary>
    public static string? FindWindowsTerminal()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        return File.Exists(alias) ? alias : null;
    }
}
