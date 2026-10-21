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
            return InCommonDir(PathUtil.Relative(path, layout.CommonDir), layout);

        // A folder appearing or disappearing in a container may be a new worktree or a leftover.
        if (Path.GetDirectoryName(path) is { } parent && layout.Containers.Any(c => c.Equals(parent, StringComparison.OrdinalIgnoreCase)))
            return ChangeSet.WorktreeListChanged;

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

    private static ChangeSet InCommonDir(string? relative, WatchLayout layout)
    {
        if (relative is null)
            return ChangeSet.None;

        var parts = relative.Split('/');
        switch (parts)
        {
            case ["HEAD" or "index"]:
                return layout.Worktrees.FirstOrDefault(w => SameDir(w.GitDir, layout.CommonDir)) is { } main
                    ? ChangeSet.HeadChanged(main.Path)
                    : ChangeSet.None;
            case ["packed-refs"]:
            case ["refs", ..]:
                return ChangeSet.RefsChanged;
            case ["worktrees"]:
            case ["worktrees", _]:
            case ["worktrees", _, "locked" or "gitdir"]:
                return ChangeSet.WorktreeListChanged;
            case ["worktrees", var name, "HEAD" or "index"]:
                var record = Path.Combine(layout.CommonDir, "worktrees", name);
                return layout.Worktrees.FirstOrDefault(w => SameDir(w.GitDir, record)) is { } linked
                    ? ChangeSet.HeadChanged(linked.Path)
                    : ChangeSet.WorktreeListChanged;
            default:
                // objects, logs, FETCH_HEAD, ORIG_HEAD, config and the like.
                return ChangeSet.None;
        }
    }

    private static bool SameDir(string? a, string b) =>
        a is not null && PathUtil.Normalize(a).Equals(PathUtil.Normalize(b), StringComparison.OrdinalIgnoreCase);
}
