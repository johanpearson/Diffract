using Diffract.Git;
using Diffract.Git.Changes;

namespace Diffract.Leftovers.Cleanup;

/// <summary>
/// Throws a file's changes away, but only after copying the file, and puts the copy back on undo. A copy is made
/// before every discard, so a discard that git refuses leaves the file as it was and the copy is removed again.
/// </summary>
internal sealed class DiscardRunner(GitRunnerOptions git, DiscardBackups backups)
{
    public async Task<DiscardOutcome> DiscardAsync(DiscardWork work, CancellationToken ct)
    {
        var full = WorktreeFile.Resolve(work.Worktree, work.Path);
        string? copy = null;
        try
        {
            if (FileState.Read(full) is { } before)
                copy = backups.Save(before);
            await ThrowAwayAsync(work, full, ct).ConfigureAwait(false);
            return DiscardOutcome.Done() with { Undo = new FileUndo(work.Worktree, work.Path, copy, FileState.HashOf(full)) };
        }
        catch (Exception e) when (e is GitException or IOException or UnauthorizedAccessException)
        {
            if (copy is not null)
                backups.Delete(copy);
            return DiscardOutcome.Failed($"{work.Path} was left as it is: {Reason(e)}");
        }
    }

    /// <summary>Writes the copy back, if the file is still as the discard left it.</summary>
    public DiscardOutcome Restore(FileUndo undo)
    {
        var full = WorktreeFile.Resolve(undo.Worktree, undo.Path);
        try
        {
            if (FileState.HashOf(full) != undo.AfterHash)
                return DiscardOutcome.Skipped($"{undo.Path} has changed since it was discarded, so Undo left it as it is.");
            if (undo.BackupName is null)
                File.Delete(full);
            else if (backups.Read(undo.BackupName) is { } bytes)
                Write(full, bytes);
            else
                return DiscardOutcome.Skipped($"The copy of {undo.Path} is gone: copies last until Diffract closes.");
            return DiscardOutcome.Done();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DiscardOutcome.Failed($"{undo.Path} was left as it is: {e.Message}");
        }
    }

    private Task ThrowAwayAsync(DiscardWork work, string full, CancellationToken ct)
    {
        var writer = new ChangesWriter(work.Worktree, git);
        switch (work.Mode)
        {
            case DiscardMode.DeleteNewFile:
                File.SetAttributes(full, FileAttributes.Normal);
                File.Delete(full);
                return Task.CompletedTask;
            case DiscardMode.ReverseHunk:
                return writer.DiscardPatchAsync(work.Patch ?? throw new ArgumentException("A hunk discard needs its patch."), work.Path, ct);
            default:
                return writer.DiscardFilesAsync([work.Path], ct);
        }
    }

    private static void Write(string full, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private static string Reason(Exception e) => e is GitCommandException failed && failed.StandardError.Trim() is { Length: > 0 } said ? said : e.Message;
}
