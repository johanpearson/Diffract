using Diffract.Git.Parsing;

namespace Diffract.Git.Changes;

/// <summary>What sort of entry a changed file is, which decides what can be done with it.</summary>
public enum ChangedFileKind
{
    /// <summary>A file git tracks, or one added to the index.</summary>
    Tracked,

    /// <summary>A file git does not track yet.</summary>
    Untracked,

    /// <summary>Unmerged after a merge, rebase or cherry-pick that stopped. Listed, not resolved (milestone 7).</summary>
    Conflicted,

    /// <summary>A submodule. Its content is a commit in another repo, so it has no diff here.</summary>
    Submodule,

    /// <summary>
    /// An untracked folder that holds a repo or worktree of its own, such as an agent's worktree inside this one.
    /// Git lists it as one entry. It has no diff, and staging it would add it as a submodule.
    /// </summary>
    NestedRepo,
}

/// <summary>One file that differs between HEAD, the index and the worktree.</summary>
public sealed record ChangedFile
{
    private static readonly Dictionary<char, FileChangeKind> Changes = new()
    {
        ['M'] = FileChangeKind.Modified,
        ['T'] = FileChangeKind.TypeChanged,
        ['A'] = FileChangeKind.Added,
        ['D'] = FileChangeKind.Deleted,
        ['R'] = FileChangeKind.Renamed,
        ['C'] = FileChangeKind.Copied,
    };

    /// <summary>Relative to the worktree, with forward slashes.</summary>
    public required string Path { get; init; }

    /// <summary>The path before a rename or copy. Null otherwise.</summary>
    public string? OldPath { get; init; }

    public required ChangedFileKind Kind { get; init; }

    /// <summary>How the index differs from HEAD. Null when it does not.</summary>
    public FileChangeKind? Staged { get; init; }

    /// <summary>How the worktree differs from the index. Null when it does not. Added for untracked files.</summary>
    public FileChangeKind? Unstaged { get; init; }

    /// <summary>The file's blob at HEAD, for the old side of a staged diff. Null when HEAD has no such file.</summary>
    public string? HeadId { get; init; }

    /// <summary>The file's blob in the index, for the new side of a staged diff and the old side of an unstaged one.</summary>
    public string? IndexId { get; init; }

    /// <summary>The file as Changes shows it. Ignored files are not shown and give null.</summary>
    public static ChangedFile? From(StatusEntry entry) => entry.Kind switch
    {
        StatusEntryKind.Untracked when entry.Path.EndsWith('/') => Untracked(entry.Path.TrimEnd('/'), ChangedFileKind.NestedRepo),
        StatusEntryKind.Untracked => Untracked(entry.Path, ChangedFileKind.Untracked),
        StatusEntryKind.Unmerged => new ChangedFile { Path = entry.Path, Kind = ChangedFileKind.Conflicted },
        StatusEntryKind.Changed or StatusEntryKind.RenamedOrCopied => Tracked(entry),
        _ => null,
    };

    private static ChangedFile Untracked(string path, ChangedFileKind kind) =>
        new() { Path = path, Kind = kind, Unstaged = FileChangeKind.Added };

    /// <summary>A submodule's ids are commits in another repo, with nothing to read here, so it gets none.</summary>
    private static ChangedFile Tracked(StatusEntry entry) => new()
    {
        Path = entry.Path,
        OldPath = entry.OriginalPath,
        Kind = entry.IsSubmodule ? ChangedFileKind.Submodule : ChangedFileKind.Tracked,
        Staged = ChangeOf(entry.IndexStatus),
        Unstaged = ChangeOf(entry.WorktreeStatus),
        HeadId = entry.IsSubmodule ? null : entry.HeadId,
        IndexId = entry.IsSubmodule ? null : entry.IndexId,
    };

    /// <summary>The X or Y letter of porcelain v2. A dot means no change.</summary>
    private static FileChangeKind? ChangeOf(char status) =>
        status == '.' ? null : Changes.GetValueOrDefault(status, FileChangeKind.Unknown);
}

/// <summary>The state of one worktree, as the Changes view shows it.</summary>
public sealed record WorktreeChanges
{
    /// <summary>The worktree's full path.</summary>
    public required string Worktree { get; init; }

    /// <summary>The checked-out branch. Null when HEAD is detached.</summary>
    public string? Branch { get; init; }

    /// <summary>The commit at HEAD. Null before the first commit.</summary>
    public string? HeadCommit { get; init; }

    /// <summary>Such as "origin/main". Null when the branch has none.</summary>
    public string? Upstream { get; init; }

    /// <summary>Commits on the branch that its upstream lacks. Null without an upstream, or when it is gone.</summary>
    public int? Ahead { get; init; }

    /// <summary>Commits on the upstream that the branch lacks.</summary>
    public int? Behind { get; init; }

    /// <summary>
    /// The remote-tracking branch that already has the commit at HEAD, such as "origin/main". Null when none has it,
    /// and before the first commit. Amend is allowed only when this is null.
    /// </summary>
    public string? HeadOnRemote { get; init; }

    /// <summary>In git's order: tracked files by path, then untracked ones.</summary>
    public required IReadOnlyList<ChangedFile> Files { get; init; }

    public static WorktreeChanges From(string worktree, StatusReport status) => new()
    {
        Worktree = worktree,
        Branch = status.Branch.Head,
        HeadCommit = status.Branch.Oid,
        Upstream = status.Branch.Upstream,
        Ahead = status.Branch.Ahead,
        Behind = status.Branch.Behind,
        Files = [.. status.Entries.Select(ChangedFile.From).OfType<ChangedFile>()],
    };
}
