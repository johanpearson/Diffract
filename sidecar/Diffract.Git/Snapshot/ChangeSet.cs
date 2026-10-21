namespace Diffract.Git.Snapshot;

/// <summary>
/// What changed in a repo since its last snapshot, so a refresh can update only that part.
/// Changes that arrive together are merged with <see cref="Merge"/>. Worktree paths are full paths.
/// </summary>
/// <param name="Everything">The watcher lost track of changes. Rebuild everything except unreachable commits.</param>
/// <param name="WorktreeList">Worktrees were added, removed, locked or unlocked, or a worktree container changed.</param>
/// <param name="Refs">Refs or packed-refs changed.</param>
/// <param name="WorktreeHeads">Worktrees whose HEAD or index changed.</param>
/// <param name="WorktreeFiles">Worktrees with changed files.</param>
public sealed record ChangeSet(
    bool Everything,
    bool WorktreeList,
    bool Refs,
    IReadOnlySet<string> WorktreeHeads,
    IReadOnlySet<string> WorktreeFiles)
{
    public static ChangeSet None { get; } = new(false, false, false, Paths(), Paths());

    public static ChangeSet All { get; } = None with { Everything = true };

    public static ChangeSet WorktreeListChanged { get; } = None with { WorktreeList = true };

    public static ChangeSet RefsChanged { get; } = None with { Refs = true };

    public static ChangeSet HeadChanged(string worktree) => None with { WorktreeHeads = Paths(worktree) };

    public static ChangeSet FilesChanged(string worktree) => None with { WorktreeFiles = Paths(worktree) };

    public bool IsEmpty => !Everything && !WorktreeList && !Refs && WorktreeHeads.Count == 0 && WorktreeFiles.Count == 0;

    public ChangeSet Merge(ChangeSet other) => new(
        Everything || other.Everything,
        WorktreeList || other.WorktreeList,
        Refs || other.Refs,
        Paths([.. WorktreeHeads, .. other.WorktreeHeads]),
        Paths([.. WorktreeFiles, .. other.WorktreeFiles]));

    public override string ToString()
    {
        List<string> parts = [];
        if (Everything) parts.Add("everything");
        if (WorktreeList) parts.Add("worktree list");
        if (Refs) parts.Add("refs");
        parts.AddRange(WorktreeHeads.Order(StringComparer.OrdinalIgnoreCase).Select(p => $"HEAD of {p}"));
        parts.AddRange(WorktreeFiles.Order(StringComparer.OrdinalIgnoreCase).Select(p => $"files in {p}"));
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    private static HashSet<string> Paths(params string[] paths) => new(paths, StringComparer.OrdinalIgnoreCase);
}
