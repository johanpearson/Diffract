namespace Diffract.Git.Snapshot;

/// <summary>Finds a worktree's git directory by reading files, without starting git.</summary>
public static class GitDirectory
{
    /// <summary>
    /// The index file of a worktree: <c>.git/index</c> for the main worktree, and
    /// <c>&lt;gitdir&gt;/index</c> for a linked one, where <c>.git</c> is a file holding "gitdir: &lt;path&gt;".
    /// </summary>
    public static string? FindIndexFile(string worktreePath)
    {
        var dotGit = Path.Combine(worktreePath, ".git");
        if (Directory.Exists(dotGit))
            return Existing(Path.Combine(dotGit, "index"));

        return ReadGitFileLink(worktreePath) is { } gitDir ? Existing(Path.Combine(gitDir, "index")) : null;
    }

    /// <summary>
    /// Where git takes the worktree's index lock: <c>index.lock</c> next to its index. Null when the git directory
    /// cannot be found. Diffract only ever checks for it, and never removes it.
    /// </summary>
    public static string? FindIndexLock(string worktreePath)
    {
        var dotGit = Path.Combine(worktreePath, ".git");
        var gitDir = Directory.Exists(dotGit) ? dotGit : ReadGitFileLink(worktreePath);
        return gitDir is null ? null : Path.Combine(gitDir, "index.lock");
    }

    /// <summary>
    /// The full path in a <c>.git</c> file holding "gitdir: &lt;path&gt;", as linked worktrees have.
    /// Null when there is no such file. Reads that one file and nothing else in the folder.
    /// </summary>
    public static string? ReadGitFileLink(string folder)
    {
        var dotGit = Path.Combine(folder, ".git");
        if (!File.Exists(dotGit))
            return null;

        const string prefix = "gitdir:";
        string? line;
        try
        {
            line = File.ReadLines(dotGit).FirstOrDefault()?.Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (line is null || !line.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(line[prefix.Length..].Trim(), folder));
    }

    /// <summary>True when the folder has a .git file or folder, so git (or a tool) once used it as a worktree or repo.</summary>
    public static bool HasGitEntry(string folder) =>
        File.Exists(Path.Combine(folder, ".git")) || Directory.Exists(Path.Combine(folder, ".git"));

    /// <summary>
    /// For a path like <c>C:\src\repo\.git\worktrees\name</c>, the repo it belongs to: <c>C:\src\repo</c>,
    /// or the git directory itself for a bare repo. Null when the path is not a worktree record.
    /// </summary>
    public static string? OwnerOfWorktreeRecord(string recordPath)
    {
        var worktrees = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(recordPath));
        if (worktrees is null || !Path.GetFileName(worktrees).Equals("worktrees", StringComparison.OrdinalIgnoreCase))
            return null;

        var gitDir = Path.GetDirectoryName(worktrees);
        if (gitDir is null)
            return null;
        return Path.GetFileName(gitDir).Equals(".git", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(gitDir) : gitDir;
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;
}
