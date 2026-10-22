namespace Diffract.Git.Snapshot;

/// <summary>
/// What changed in a repo since its last snapshot, so a refresh can update only that part.
/// Changes that arrive together are merged with <see cref="Merge"/>. Worktree paths are full paths.
/// </summary>
public sealed record ChangeSet
{
    /// <summary>The watcher lost track of changes. Rebuild everything except unreachable commits.</summary>
    public bool Everything { get; init; }

    /// <summary>Worktrees were added, removed, locked or unlocked, or a worktree container changed.</summary>
    public bool WorktreeList { get; init; }

    /// <summary>Refs or packed-refs changed.</summary>
    public bool Refs { get; init; }

    /// <summary>Worktrees whose HEAD or index changed.</summary>
    public IReadOnlySet<string> WorktreeHeads { get; init; } = Paths();

    /// <summary>Worktrees with changed files.</summary>
    public IReadOnlySet<string> WorktreeFiles { get; init; } = Paths();

    public static ChangeSet None { get; } = new();

    public static ChangeSet All { get; } = None with { Everything = true };

    public static ChangeSet WorktreeListChanged { get; } = None with { WorktreeList = true };

    public static ChangeSet RefsChanged { get; } = None with { Refs = true };

    public static ChangeSet HeadChanged(string worktree) => None with { WorktreeHeads = Paths(worktree) };

    public static ChangeSet FilesChanged(string worktree) => None with { WorktreeFiles = Paths(worktree) };

    public bool IsEmpty => !Everything && !WorktreeList && !Refs && WorktreeHeads.Count == 0 && WorktreeFiles.Count == 0;

    public ChangeSet Merge(ChangeSet other) => new()
    {
        Everything = Everything || other.Everything,
        WorktreeList = WorktreeList || other.WorktreeList,
        Refs = Refs || other.Refs,
        WorktreeHeads = Paths([.. WorktreeHeads, .. other.WorktreeHeads]),
        WorktreeFiles = Paths([.. WorktreeFiles, .. other.WorktreeFiles]),
    };

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
