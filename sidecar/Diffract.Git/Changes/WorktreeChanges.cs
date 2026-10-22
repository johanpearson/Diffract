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
/// <param name="Path">Relative to the worktree, with forward slashes.</param>
/// <param name="OldPath">The path before a rename or copy. Null otherwise.</param>
/// <param name="Staged">How the index differs from HEAD. Null when it does not.</param>
/// <param name="Unstaged">How the worktree differs from the index. Null when it does not. Added for untracked files.</param>
/// <param name="HeadId">The file's blob at HEAD, for the old side of a staged diff. Null when HEAD has no such file.</param>
/// <param name="IndexId">The file's blob in the index, for the new side of a staged diff and the old side of an unstaged one.</param>
public sealed record ChangedFile(
    string Path,
    string? OldPath,
    ChangedFileKind Kind,
    FileChangeKind? Staged,
    FileChangeKind? Unstaged,
    string? HeadId,
    string? IndexId)
{
    /// <summary>The file as Changes shows it. Ignored files are not shown and give null.</summary>
    public static ChangedFile? From(StatusEntry entry) => entry.Kind switch
    {
        StatusEntryKind.Untracked when entry.Path.EndsWith('/') => Untracked(entry.Path.TrimEnd('/'), ChangedFileKind.NestedRepo),
        StatusEntryKind.Untracked => Untracked(entry.Path, ChangedFileKind.Untracked),
        StatusEntryKind.Unmerged => new ChangedFile(entry.Path, null, ChangedFileKind.Conflicted, null, null, null, null),
        StatusEntryKind.Changed or StatusEntryKind.RenamedOrCopied => Tracked(entry),
        _ => null,
    };

    private static ChangedFile Untracked(string path, ChangedFileKind kind) =>
        new(path, null, kind, null, FileChangeKind.Added, null, null);

    /// <summary>A submodule's ids are commits in another repo, with nothing to read here, so it gets none.</summary>
    private static ChangedFile Tracked(StatusEntry entry) => new(
        entry.Path,
        entry.OriginalPath,
        entry.IsSubmodule ? ChangedFileKind.Submodule : ChangedFileKind.Tracked,
        ChangeOf(entry.IndexStatus),
        ChangeOf(entry.WorktreeStatus),
        entry.IsSubmodule ? null : entry.HeadId,
        entry.IsSubmodule ? null : entry.IndexId);

    /// <summary>The X or Y letter of porcelain v2. A dot means no change.</summary>
    private static FileChangeKind? ChangeOf(char status) => status switch
    {
        '.' => null,
        'M' => FileChangeKind.Modified,
        'T' => FileChangeKind.TypeChanged,
        'A' => FileChangeKind.Added,
        'D' => FileChangeKind.Deleted,
        'R' => FileChangeKind.Renamed,
        'C' => FileChangeKind.Copied,
        _ => FileChangeKind.Unknown,
    };
}

/// <summary>The state of one worktree, as the Changes view shows it.</summary>
/// <param name="Worktree">The worktree's full path.</param>
/// <param name="Branch">The checked-out branch. Null when HEAD is detached.</param>
/// <param name="HeadCommit">The commit at HEAD. Null before the first commit.</param>
/// <param name="Upstream">Such as "origin/main". Null when the branch has none.</param>
/// <param name="Ahead">Commits on the branch that its upstream lacks. Null without an upstream, or when it is gone.</param>
/// <param name="Behind">Commits on the upstream that the branch lacks.</param>
/// <param name="Files">In git's order: by path.</param>
public sealed record WorktreeChanges(
    string Worktree,
    string? Branch,
    string? HeadCommit,
    string? Upstream,
    int? Ahead,
    int? Behind,
    IReadOnlyList<ChangedFile> Files)
{
    public static WorktreeChanges From(string worktree, StatusReport status) => new(
        worktree,
        status.Branch.Head,
        status.Branch.Oid,
        status.Branch.Upstream,
        status.Branch.Ahead,
        status.Branch.Behind,
        [.. status.Entries.Select(ChangedFile.From).OfType<ChangedFile>()]);
}
