namespace Diffract.Sidecar;

/// <summary>Where Git for Windows is installed, and which programs are using it.</summary>
internal static class GitForWindows
{
    /// <summary>
    /// The folder that holds git-bash.exe. It sits a level or two above the git.exe that is on PATH (cmd\git.exe or
    /// mingw64\bin\git.exe). The default install folder is the fallback. Null when neither is there.
    /// </summary>
    public static string? FindFolder(MachineLayout machine)
    {
        var gitFolders = (machine.Path ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(dir => Path.IsPathFullyQualified(dir) && machine.FileExists(Path.Combine(dir, "git.exe")));

        foreach (var folder in gitFolders)
        {
            // Two levels up at most: cmd\ and mingw64\bin\ are the layouts Git for Windows has.
            var dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder));
            for (var level = 0; level < 2 && dir is not null; level++, dir = Path.GetDirectoryName(dir))
            {
                if (machine.FileExists(Path.Combine(dir, "git-bash.exe")))
                    return dir;
            }
        }
        var fallback = Path.Combine(machine.ProgramFiles, "Git");
        return machine.FileExists(Path.Combine(fallback, "git-bash.exe")) ? fallback : null;
    }

    /// <summary>
    /// The programs running something from Git for Windows' folder, such as an editor's long-running
    /// <c>git check-ignore</c> or a Git Bash window. Its installer will not replace git while they run.
    /// Diffract's own git calls are left out: they are short, and Diffract pauses them during an update.
    /// </summary>
    /// <param name="selfId">The sidecar's own process id.</param>
    public static IReadOnlyList<GitInUse> Users(IProcessTable table, string gitFolder, int selfId)
    {
        var processes = table.Snapshot();
        var byId = new Dictionary<int, ProcessInfo>();
        foreach (var process in processes)
            byId.TryAdd(process.Id, process);

        var root = Path.TrimEndingDirectorySeparator(gitFolder) + Path.DirectorySeparatorChar;
        bool InGit(ProcessInfo p) => p.Path?.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true;
        ProcessInfo? Parent(ProcessInfo p) => p.ParentId != p.Id && byId.TryGetValue(p.ParentId, out var parent) ? parent : null;

        var users = new Dictionary<(string Program, string? StartedBy), int>();
        foreach (var process in processes.Where(InGit))
        {
            // Climb past git's own processes to the program that started them. The step limit guards against
            // a loop, which reused process ids can make.
            var top = process;
            var starter = Parent(process);
            var mine = process.Id == selfId;
            for (var steps = 0; starter is not null && steps < 64; steps++)
            {
                mine |= starter.Id == selfId;
                if (!InGit(starter))
                    break;
                (top, starter) = (starter, Parent(starter));
            }
            if (mine || (starter is not null && IsSelfOrBelow(starter, selfId, Parent)))
                continue;

            // Started from the Start menu or a shortcut, the program is Git for Windows' own, such as a Git Bash window.
            if (starter?.Path is { } path && IsExplorer(path))
                starter = null;

            var key = starter?.Path is { } starterPath
                ? (table.Describe(starterPath), StartedBy(table, Parent(starter), table.Describe(starterPath)))
                : (OwnName(table, top.Path!), null);
            users[key] = users.GetValueOrDefault(key) + 1;
        }
        return [.. users.Select(u => new GitInUse(u.Key.Program, u.Key.StartedBy, u.Value)).OrderBy(u => u.Program, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The program one level up, when it says something new: not the same program, and not Explorer.</summary>
    private static string? StartedBy(IProcessTable table, ProcessInfo? parent, string program)
    {
        if (parent?.Path is not { } path || IsExplorer(path))
            return null;
        var name = table.Describe(path);
        return name.Equals(program, StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    /// <summary>A Git Bash window runs in mintty, which git-bash.exe starts. Anything else goes by its own name.</summary>
    private static string OwnName(IProcessTable table, string path) =>
        Path.GetFileName(path).ToLowerInvariant() is "mintty.exe" or "git-bash.exe" ? "Git Bash" : table.Describe(path);

    private static bool IsExplorer(string path) => Path.GetFileName(path).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsSelfOrBelow(ProcessInfo process, int selfId, Func<ProcessInfo, ProcessInfo?> parent)
    {
        for (var (p, steps) = (process, 0); p is not null && steps < 64; p = parent(p), steps++)
        {
            if (p.Id == selfId)
                return true;
        }
        return false;
    }
}

/// <param name="Program">The program using git, such as "Visual Studio Code" or "Git Bash".</param>
/// <param name="StartedBy">The program that started it, when that tells more. Null otherwise.</param>
/// <param name="Processes">How many of Git for Windows' processes it is running.</param>
internal sealed record GitInUse(string Program, string? StartedBy, int Processes);
