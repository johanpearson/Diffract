using Diffract.Git.Snapshot;

namespace Diffract.Git.Watching;

/// <param name="Path">The worktree folder.</param>
/// <param name="GitDir">Its own git directory: the common git directory for the main worktree, <c>worktrees/&lt;name&gt;</c> for a linked one.</param>
/// <param name="HasFiles">False for a bare repo or a worktree whose folder is gone. Its files are not watched.</param>
public sealed record WatchedWorktree(string Path, string? GitDir, bool HasFiles);

/// <summary>
/// Where a repo's changes show up: the common git directory, each worktree, and the folders that hold worktrees.
/// Built from a snapshot by reading files only, never by starting git.
/// </summary>
/// <param name="CommonDir">Where HEAD of the main worktree, refs, packed-refs and worktree records live.</param>
/// <param name="Containers">Folders whose subfolders are worktrees or leftovers of them. Watched without subfolders.</param>
public sealed record WatchLayout(string CommonDir, IReadOnlyList<WatchedWorktree> Worktrees, IReadOnlyList<string> Containers)
{
    /// <summary>Null when the common git directory cannot be found from the files, for example when .git was deleted.</summary>
    public static WatchLayout? From(RepoSnapshot snapshot, IReadOnlyList<string> containerPatterns)
    {
        var main = snapshot.Worktrees[0];
        if (GitDirOf(main) is not { } commonDir)
            return null;

        var worktrees = snapshot.Worktrees
            .Select(w => new WatchedWorktree(w.Path, w.IsMain ? commonDir : GitDirOf(w), !w.IsBare && !w.IsPrunable && Directory.Exists(w.Path)))
            .ToList();
        return new WatchLayout(commonDir, worktrees, RepoSnapshotBuilder.WorktreeContainers(snapshot.Worktrees, containerPatterns));
    }

    /// <summary>The folders to watch: recursive ones that are not inside another recursive one, then the containers.</summary>
    public IReadOnlyList<(string Path, bool Recursive)> Roots()
    {
        var recursive = new List<string>();
        foreach (var path in Worktrees.Where(w => w.HasFiles).Select(w => w.Path).Append(CommonDir).OrderBy(p => p.Length))
        {
            if (!recursive.Any(r => PathUtil.IsSameOrInside(path, r)))
                recursive.Add(path);
        }

        return [.. recursive.Select(p => (p, true)), .. Containers.Where(c => !recursive.Any(r => PathUtil.IsSameOrInside(c, r))).Select(c => (c, false))];
    }

    private static string? GitDirOf(WorktreeInfo worktree)
    {
        if (worktree.IsBare)
            return worktree.Path;
        var dotGit = System.IO.Path.Combine(worktree.Path, ".git");
        return Directory.Exists(dotGit) ? dotGit : GitDirectory.ReadGitFileLink(worktree.Path);
    }
}
