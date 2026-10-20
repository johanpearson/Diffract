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
        if (!File.Exists(dotGit))
            return null;

        const string prefix = "gitdir:";
        var line = File.ReadLines(dotGit).FirstOrDefault()?.Trim();
        if (line is null || !line.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var gitDir = Path.GetFullPath(line[prefix.Length..].Trim(), worktreePath);
        return Existing(Path.Combine(gitDir, "index"));
    }

    /// <summary>True when the folder has a .git file or folder, so git (or a tool) once used it as a worktree or repo.</summary>
    public static bool HasGitEntry(string folder) =>
        File.Exists(Path.Combine(folder, ".git")) || Directory.Exists(Path.Combine(folder, ".git"));

    private static string? Existing(string path) => File.Exists(path) ? path : null;
}
