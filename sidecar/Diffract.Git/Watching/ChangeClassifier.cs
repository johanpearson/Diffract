using Diffract.Git.Snapshot;

namespace Diffract.Git.Watching;

/// <summary>
/// Turns one changed path into what it means for the snapshot. A pure function over the path and the layout:
/// it reads no files and starts no git. Paths that matter to nothing give <see cref="ChangeSet.None"/>.
/// </summary>
public static class ChangeClassifier
{
    public static ChangeSet Classify(string path, WatchLayout layout)
    {
        path = PathUtil.Normalize(path);

        // Git writes through a .lock file and renames it into place; the rename is what counts.
        if (path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            return ChangeSet.None;

        if (PathUtil.IsSameOrInside(path, layout.CommonDir))
            return InCommonDir(PathUtil.Relative(path, layout.CommonDir)?.Split('/'), layout);

        // A folder appearing or disappearing in a container may be a new worktree or a leftover.
        if (Path.GetDirectoryName(path) is { } parent && layout.Containers.Any(c => c.Equals(parent, StringComparison.OrdinalIgnoreCase)))
            return ChangeSet.WorktreeListChanged;

        return InWorktree(path, layout);
    }

    private static ChangeSet InWorktree(string path, WatchLayout layout)
    {
        var worktree = layout.Worktrees
            .Where(w => w.HasFiles && PathUtil.IsSameOrInside(path, w.Path))
            .MaxBy(w => w.Path.Length);
        if (worktree is null)
            return ChangeSet.None;

        // A linked worktree's .git file, or anything in a git directory kept inside the worktree, is not a file change.
        var relative = PathUtil.Relative(path, worktree.Path);
        if (relative is ".git" || relative?.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) == true)
            return ChangeSet.None;

        return ChangeSet.FilesChanged(worktree.Path);
    }

    private static ChangeSet InCommonDir(string[]? parts, WatchLayout layout) => parts switch
    {
        null => ChangeSet.None,
        ["HEAD" or "index"] => HeadChanged(WithGitDir(layout, layout.CommonDir), otherwise: ChangeSet.None),
        ["packed-refs"] or ["refs", ..] => ChangeSet.RefsChanged,
        ["worktrees"] or ["worktrees", _] or ["worktrees", _, "locked" or "gitdir"] => ChangeSet.WorktreeListChanged,
        ["worktrees", var name, "HEAD" or "index"] =>
            HeadChanged(WithGitDir(layout, Path.Combine(layout.CommonDir, "worktrees", name)), otherwise: ChangeSet.WorktreeListChanged),
        // objects, logs, FETCH_HEAD, ORIG_HEAD, config and the like.
        _ => ChangeSet.None,
    };

    private static ChangeSet HeadChanged(WatchedWorktree? worktree, ChangeSet otherwise) =>
        worktree is null ? otherwise : ChangeSet.HeadChanged(worktree.Path);

    private static WatchedWorktree? WithGitDir(WatchLayout layout, string gitDir) =>
        layout.Worktrees.FirstOrDefault(w => w.GitDir is { } own && PathUtil.Normalize(own).Equals(PathUtil.Normalize(gitDir), StringComparison.OrdinalIgnoreCase));
}
