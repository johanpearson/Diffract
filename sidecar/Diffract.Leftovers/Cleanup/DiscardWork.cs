namespace Diffract.Leftovers.Cleanup;

/// <summary>How a discard throws a change away.</summary>
public enum DiscardMode
{
    /// <summary>A tracked file goes back to what the index has. Staged changes stay.</summary>
    RestoreFromIndex,

    /// <summary>A file git does not track yet is deleted.</summary>
    DeleteNewFile,

    /// <summary>One hunk of the unstaged diff is reversed in the working tree.</summary>
    ReverseHunk,
}

/// <summary>One file's discard, as a plan holds it.</summary>
/// <param name="Worktree">The worktree's full path. Git runs there and nowhere else.</param>
/// <param name="Path">The file, relative to the worktree, with forward slashes.</param>
/// <param name="Patch">For <see cref="DiscardMode.ReverseHunk"/>, the patch that reverses the hunk, read when the plan was made.</param>
public sealed record DiscardWork(string Worktree, string Path, DiscardMode Mode, byte[]? Patch);

/// <summary>
/// What takes a discard back: the copy Diffract made, and the file as the discard left it, which Undo requires so it
/// never overwrites newer work. Kept in the operation log.
/// </summary>
/// <param name="BackupName">The copy of the file before the discard. Null when there was no file, so Undo deletes it again.</param>
/// <param name="AfterHash">The SHA-256 of the file after the discard. Null when there was no file.</param>
public sealed record FileUndo(string Worktree, string Path, string? BackupName, string? AfterHash);
