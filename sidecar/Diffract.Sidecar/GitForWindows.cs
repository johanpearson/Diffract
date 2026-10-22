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
        var tree = new ProcessTree(table.Snapshot(), gitFolder, selfId);
        var users = new Dictionary<(string Program, string? StartedBy), int>();
        foreach (var process in tree.InGitFolder())
        {
            if (tree.Climb(process) is not { } chain)
                continue;

            var key = UserOf(table, tree, chain);
            users[key] = users.GetValueOrDefault(key) + 1;
        }
        return [.. users.Select(u => new GitInUse(u.Key.Program, u.Key.StartedBy, u.Value)).OrderBy(u => u.Program, StringComparer.OrdinalIgnoreCase)];
    }

    private static (string Program, string? StartedBy) UserOf(IProcessTable table, ProcessTree tree, ProcessChain chain) =>
        chain.Starter?.Path is { } starterPath
            ? (table.Describe(starterPath), StartedBy(table, tree.Parent(chain.Starter), table.Describe(starterPath)))
            : (OwnName(table, chain.Top.Path!), null);

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

    /// <param name="Top">The last of git's own processes in the chain.</param>
    /// <param name="Starter">The program that started it, or null when that is Git for Windows itself.</param>
    private sealed record ProcessChain(ProcessInfo Top, ProcessInfo? Starter);

    /// <summary>The process list, with each process's parent, as seen from Git for Windows' folder.</summary>
    private sealed class ProcessTree
    {
        // The step limit guards against a loop, which reused process ids can make.
        private const int MaxSteps = 64;

        private readonly IReadOnlyList<ProcessInfo> _processes;
        private readonly Dictionary<int, ProcessInfo> _byId = [];
        private readonly string _root;
        private readonly int _selfId;

        public ProcessTree(IReadOnlyList<ProcessInfo> processes, string gitFolder, int selfId)
        {
            _processes = processes;
            foreach (var process in processes)
                _byId.TryAdd(process.Id, process);
            _root = Path.TrimEndingDirectorySeparator(gitFolder) + Path.DirectorySeparatorChar;
            _selfId = selfId;
        }

        public IEnumerable<ProcessInfo> InGitFolder() => _processes.Where(InGit);

        public ProcessInfo? Parent(ProcessInfo p) => p.ParentId != p.Id && _byId.TryGetValue(p.ParentId, out var parent) ? parent : null;

        /// <summary>
        /// Climbs past git's own processes to the program that started them. Null when the chain is Diffract's own.
        /// </summary>
        public ProcessChain? Climb(ProcessInfo process)
        {
            var top = process;
            var starter = Parent(process);
            var mine = process.Id == _selfId;
            for (var steps = 0; starter is not null && steps < MaxSteps; steps++)
            {
                mine |= starter.Id == _selfId;
                if (!InGit(starter))
                    break;
                (top, starter) = (starter, Parent(starter));
            }
            return mine || IsSelfOrBelow(starter) ? null : new ProcessChain(top, NotExplorer(starter));
        }

        // Started from the Start menu or a shortcut, the program is Git for Windows' own, such as a Git Bash window.
        private static ProcessInfo? NotExplorer(ProcessInfo? starter) =>
            starter?.Path is { } path && IsExplorer(path) ? null : starter;

        private bool InGit(ProcessInfo p) => p.Path?.StartsWith(_root, StringComparison.OrdinalIgnoreCase) == true;

        private bool IsSelfOrBelow(ProcessInfo? process)
        {
            for (var (p, steps) = (process, 0); p is not null && steps < MaxSteps; p = Parent(p), steps++)
            {
                if (p.Id == _selfId)
                    return true;
            }
            return false;
        }
    }
}

/// <param name="Program">The program using git, such as "Visual Studio Code" or "Git Bash".</param>
/// <param name="StartedBy">The program that started it, when that tells more. Null otherwise.</param>
/// <param name="Processes">How many of Git for Windows' processes it is running.</param>
internal sealed record GitInUse(string Program, string? StartedBy, int Processes);
