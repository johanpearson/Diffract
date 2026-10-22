namespace Diffract.Sidecar;

/// <summary>The terminals Diffract can open. Windows Terminal comes first because it is the default when installed.</summary>
internal enum TerminalKind { WindowsTerminal, WindowsPowerShell, PowerShell7, CommandPrompt, GitBash }

/// <param name="Id">What the UI sends back to open this one.</param>
/// <param name="Name">How the UI shows it.</param>
internal sealed record TerminalInfo(TerminalKind Id, string Name);

/// <summary>Where Windows keeps things, so tests can describe a machine without touching the real one.</summary>
/// <param name="Path">The PATH variable, searched for git to find Git Bash next to it.</param>
internal sealed record MachineLayout(string SystemDirectory, string LocalAppData, string ProgramFiles, string? Path, Func<string, bool> FileExists)
{
    public static MachineLayout Current { get; } = new(
        Environment.SystemDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetEnvironmentVariable("PATH"),
        File.Exists);
}

/// <summary>
/// Finds the installed terminals and opens one in a folder. Every program starts from its full path with its
/// arguments in <c>ArgumentList</c>. Never <c>cmd /c</c>, and never a command line built from strings.
/// </summary>
internal sealed class TerminalLauncher(IProcessStarter processes, MachineLayout machine)
{
    private static readonly Dictionary<TerminalKind, string> Names = new()
    {
        [TerminalKind.WindowsTerminal] = "Windows Terminal",
        [TerminalKind.WindowsPowerShell] = "Windows PowerShell",
        [TerminalKind.PowerShell7] = "PowerShell 7",
        [TerminalKind.CommandPrompt] = "Command Prompt",
        [TerminalKind.GitBash] = "Git Bash",
    };

    public TerminalLauncher(IProcessStarter processes) : this(processes, MachineLayout.Current)
    {
    }

    /// <summary>The installed terminals, the default first. Looks at a few files each time, so a new install shows up.</summary>
    public IReadOnlyList<TerminalInfo> List() =>
        [.. Enum.GetValues<TerminalKind>().Where(k => Find(k) is not null).Select(k => new TerminalInfo(k, Names[k]))];

    /// <param name="folder">An existing, fully qualified folder.</param>
    /// <param name="terminal">Null for the default: Windows Terminal when installed, otherwise Windows PowerShell.</param>
    /// <exception cref="ArgumentException">The terminal is not installed.</exception>
    public void Open(string folder, TerminalKind? terminal = null)
    {
        var kind = Choose(folder, terminal);
        var program = Find(kind) ?? throw new ArgumentException($"{Names[kind]} is not installed.");
        Start(kind, program, folder);
    }

    private TerminalKind Choose(string folder, TerminalKind? terminal)
    {
        var kind = terminal ?? (Find(TerminalKind.WindowsTerminal) is null ? TerminalKind.WindowsPowerShell : TerminalKind.WindowsTerminal);

        // Windows Terminal reads ";" as "start another command", even inside a quoted path, so such a folder
        // would open the wrong thing. PowerShell takes the folder as its working directory, which has no such rule.
        return kind == TerminalKind.WindowsTerminal && folder.Contains(';') ? TerminalKind.WindowsPowerShell : kind;
    }

    private void Start(TerminalKind kind, string program, string folder)
    {
        switch (kind)
        {
            case TerminalKind.WindowsTerminal:
                processes.Start(program, ["-d", folder]);
                break;
            case TerminalKind.WindowsPowerShell or TerminalKind.PowerShell7:
                processes.Start(program, ["-NoLogo"], workingDirectory: folder, newConsole: true);
                break;
            case TerminalKind.CommandPrompt:
                // No arguments at all, so no /c and nothing for cmd to run: just a prompt in the folder.
                processes.Start(program, [], workingDirectory: folder, newConsole: true);
                break;
            case TerminalKind.GitBash:
                // The same form Git for Windows' own "Git Bash Here" uses. git-bash.exe opens its own window.
                processes.Start(program, [$"--cd={folder}"], workingDirectory: folder);
                break;
        }
    }

    /// <summary>The program for a terminal, by full path, or null when it is not installed.</summary>
    public string? Find(TerminalKind kind) => kind switch
    {
        // Windows Terminal installs wt.exe as an app execution alias in the user's WindowsApps folder.
        TerminalKind.WindowsTerminal => Existing(Path.Combine(machine.LocalAppData, "Microsoft", "WindowsApps", "wt.exe")),
        TerminalKind.WindowsPowerShell => Existing(Path.Combine(machine.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")),
        // The installer puts it in Program Files; the Store version adds an alias like Windows Terminal's.
        TerminalKind.PowerShell7 => Existing(Path.Combine(machine.ProgramFiles, "PowerShell", "7", "pwsh.exe"))
            ?? Existing(Path.Combine(machine.LocalAppData, "Microsoft", "WindowsApps", "pwsh.exe")),
        TerminalKind.CommandPrompt => Existing(Path.Combine(machine.SystemDirectory, "cmd.exe")),
        TerminalKind.GitBash => GitForWindows.FindFolder(machine) is { } folder ? Path.Combine(folder, "git-bash.exe") : null,
        _ => null,
    };

    private string? Existing(string path) => machine.FileExists(path) ? path : null;
}
