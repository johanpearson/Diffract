using System.Text;
using Diffract.Git;
using Diffract.Git.Changes;
using Diffract.Git.Refs;

namespace Diffract.Leftovers.Cleanup;

/// <summary>
/// Resets the current branch hard, which throws every uncommitted change away, but only after copying each file that
/// differs from the commit, and noting what was staged. Undo moves the branch back and puts the copies back, as long
/// as the branch and the files are still as the reset left them (docs/findings/hard-reset.md).
/// </summary>
public sealed class HardReset
{
    private readonly string _worktree;
    private readonly GitRunnerOptions _options;
    private readonly DiscardBackups _backups;
    private readonly ResetWriter _writer;
    private readonly GitRunner _changes;
    private readonly GitRunner _reads;
    private readonly IndexLockRetry _retry;

    public HardReset(string worktree, GitRunnerOptions options, DiscardBackups backups)
    {
        _worktree = worktree;
        _options = options;
        _backups = backups;
        _writer = new ResetWriter(worktree, options);
        _changes = new GitRunner(worktree, options with { ChangesOnly = true });
        _reads = new GitRunner(worktree, options with { ChangesOnly = false, ReadOnly = true });
        _retry = new IndexLockRetry(options.TimeProvider);
    }

    /// <summary>A file as it was just before the reset, and the copy made of it. Null name when there was no file.</summary>
    private sealed record Copy(string Path, string FullPath, string? BackupName, string? BeforeHash);

    public async Task<ResetResult> ResetAsync(string commit, CancellationToken ct)
    {
        var (branch, current) = await _writer.BranchAtAsync(commit, ct).ConfigureAwait(false);
        var status = await new ChangesReader(_options).ReadStatusAsync(_worktree, ct).ConfigureAwait(false);
        if (status.Files.Any(f => f.Kind == ChangedFileKind.Conflicted))
            throw new ChangeRefusedException("A merge, rebase or cherry-pick is stopped on a conflict here. Finish or abort it first: a hard reset would lose the conflict.");
        var staged = await ReadStagedAsync(current, ct).ConfigureAwait(false);
        var copies = CopyFiles(PathsToKeep(status.Files));
        try
        {
            await _writer.MoveFilesAsync(commit, keepLocalChanges: false, ct).ConfigureAwait(false);
        }
        catch
        {
            Delete(copies.Select(c => c.BackupName));
            throw;
        }
        return new ResetResult { Branch = branch, OldCommit = current, NewCommit = commit, Mode = ResetMode.Hard, Kept = Kept(copies, staged) };
    }

    /// <summary>
    /// Moves the branch back and puts the kept work back. Nothing is touched unless the branch is where the reset left
    /// it, every kept file is as the reset left it, and every copy is still there.
    /// </summary>
    public async Task<ResetResult> UndoAsync(ResetResult reset, CancellationToken ct)
    {
        var kept = reset.Kept ?? throw new ArgumentException("This reset kept nothing to bring back.", nameof(reset));
        await _writer.CheckStillAtAsync(reset, ct).ConfigureAwait(false);
        var files = kept.Files.Select(f => (File: f, Full: WorktreeFile.Resolve(_worktree, f.Path), Bytes: BytesOf(f))).ToList();
        foreach (var (file, full, _) in files)
            CheckRestorable(file, full);
        await _writer.MoveFilesAsync(reset.OldCommit, keepLocalChanges: true, ct).ConfigureAwait(false);
        foreach (var (_, full, bytes) in files)
            Restore(full, bytes);
        await RestoreStagedAsync(kept.Staged, ct).ConfigureAwait(false);
        Delete(kept.Files.Select(f => f.BackupName));
        return reset with { OldCommit = reset.NewCommit, NewCommit = reset.OldCommit, Kept = null };
    }

    /// <summary>Every file that differs from the commit and that a reset would change: changed, new, or the old name of a rename.</summary>
    private static IEnumerable<string> PathsToKeep(IEnumerable<ChangedFile> files) =>
        files.Where(f => f.Kind is ChangedFileKind.Tracked or ChangedFileKind.Untracked)
            .SelectMany(f => new[] { f.Path, f.OldPath })
            .OfType<string>()
            .Distinct();

    private List<Copy> CopyFiles(IEnumerable<string> paths)
    {
        var copies = new List<Copy>();
        try
        {
            foreach (var path in paths)
            {
                var full = WorktreeFile.Resolve(_worktree, path);
                var bytes = FileState.Read(full);
                copies.Add(new Copy(path, full, bytes is null ? null : _backups.Save(bytes), FileState.HashOf(bytes)));
            }
        }
        catch
        {
            Delete(copies.Select(c => c.BackupName));
            throw;
        }
        return copies;
    }

    /// <summary>Only the files the reset changed. A copy of a file it left alone is not needed, so it is removed.</summary>
    private KeptWork Kept(List<Copy> copies, IReadOnlyList<StagedEntry> staged)
    {
        var changed = copies.Select(c => (Copy: c, After: FileState.HashOf(c.FullPath))).ToList();
        Delete(changed.Where(c => c.After == c.Copy.BeforeHash).Select(c => c.Copy.BackupName));
        return new KeptWork([.. changed.Where(c => c.After != c.Copy.BeforeHash).Select(c => new KeptFile(c.Copy.Path, c.Copy.BackupName, c.After))], staged);
    }

    /// <summary>The index against the commit, as git writes it for <c>update-index --index-info</c>: mode, object id, path.</summary>
    private async Task<IReadOnlyList<StagedEntry>> ReadStagedAsync(string commit, CancellationToken ct)
    {
        var diff = await _reads.RunAsync(["diff", "--cached", "--raw", "-z", "--no-renames", "--no-abbrev", commit], ct).ConfigureAwait(false);
        var parts = diff.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var entries = new List<StagedEntry>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var fields = parts[i].TrimStart(':').Split(' ');
            entries.Add(new StagedEntry(parts[i + 1], fields[1], fields[3]));
        }
        return entries;
    }

    private async Task RestoreStagedAsync(IReadOnlyList<StagedEntry> staged, CancellationToken ct)
    {
        if (staged.Count == 0)
            return;
        var input = Encoding.UTF8.GetBytes(string.Concat(staged.Select(e => $"{e.Mode} {e.Id}\t{e.Path}\0")));
        try
        {
            await _retry.RunAsync(_changes, GitCommandLine.WithBytes(["update-index", "-z", "--index-info"], input), ct).ConfigureAwait(false);
        }
        catch (GitCommandException e)
        {
            throw new ChangeRefusedException("The branch and your files are back, but what was staged could not be: git no longer has it. Stage the files again. This is what git said:", e.StandardError);
        }
    }

    private byte[]? BytesOf(KeptFile file)
    {
        if (file.BackupName is null)
            return null;
        return _backups.Read(file.BackupName) ?? throw new ChangeRefusedException($"The copy of {file.Path} is gone: copies last until Diffract closes. Undo did nothing.");
    }

    private static void CheckRestorable(KeptFile file, string full)
    {
        if (FileState.HashOf(full) != file.AfterHash)
            throw new ChangeRefusedException($"{file.Path} has changed since the reset, so undo did nothing.");
    }

    private static void Restore(string full, byte[]? bytes)
    {
        if (bytes is null)
        {
            if (File.Exists(full))
                File.Delete(full);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private void Delete(IEnumerable<string?> names)
    {
        foreach (var name in names.OfType<string>())
            _backups.Delete(name);
    }
}
