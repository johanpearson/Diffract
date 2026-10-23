using Diffract.Git.Files;
using Diffract.Git.Parsing;

namespace Diffract.Git.Changes;

/// <summary>Which diff of a changed file to read, with what <c>changes.status</c> said about it.</summary>
public sealed record FileDiffRequest
{
    /// <summary>The file's path in the worktree, from <see cref="ChangedFile.Path"/>.</summary>
    public required string Path { get; init; }

    /// <summary>From <see cref="ChangedFile.OldPath"/>, so a staged rename's hunks are read as one diff.</summary>
    public string? OldPath { get; init; }

    /// <summary>HEAD against the index when true, the index against the worktree when false.</summary>
    public required bool Staged { get; init; }

    /// <summary>From <see cref="ChangedFile.HeadId"/>.</summary>
    public string? HeadId { get; init; }

    /// <summary>From <see cref="ChangedFile.IndexId"/>.</summary>
    public string? IndexId { get; init; }
}

/// <summary>The two sides of a file's diff, and git's hunks between them.</summary>
/// <param name="Old">Null when the file does not exist there.</param>
/// <param name="New">Null when the file does not exist there.</param>
/// <param name="Hunks">
/// Empty when either side is missing: a new or deleted file is one hunk, the whole file, and is staged as a file.
/// Empty too for a binary file.
/// </param>
public sealed record FileDiff(FileContent? Old, FileContent? New, IReadOnlyList<Hunk> Hunks);

/// <summary>
/// Reads what the Changes view shows, with as few git calls as possible: one status call per worktree, and for a
/// file's diff at most one call for each side and one for its hunks, all run side by side. Only reads.
/// </summary>
public sealed class ChangesReader
{
    private readonly GitRunnerOptions _runnerOptions;
    private readonly BlobReader _blobs;

    public ChangesReader(GitRunnerOptions runnerOptions)
    {
        if (!runnerOptions.ReadOnly)
            throw new ArgumentException("The changes reader only runs with a read-only git runner.", nameof(runnerOptions));
        _runnerOptions = runnerOptions;
        _blobs = new BlobReader(runnerOptions);
    }

    /// <summary>
    /// The worktree's branch and changed files, untracked files one by one, and whether HEAD is pushed. Two git calls,
    /// run side by side.
    /// </summary>
    public async Task<WorktreeChanges> ReadStatusAsync(string worktree, CancellationToken ct)
    {
        var runner = new GitRunner(worktree, _runnerOptions);
        var statusTask = runner.RunAsync(StatusParser.ChangesArguments, ct);
        var onRemoteTask = HeadOnRemote.ReadAsync(runner, ct);
        await Task.WhenAll(statusTask, onRemoteTask).ConfigureAwait(false);
        var status = StatusParser.Parse((await statusTask.ConfigureAwait(false)).StandardOutput);
        var onRemote = await onRemoteTask.ConfigureAwait(false);
        return WorktreeChanges.From(worktree, status) with { HeadOnRemote = status.Branch.Oid is null ? null : onRemote.Name() };
    }

    /// <summary>
    /// A staged diff has both sides in repo form, as history shows them: one git call per side. An unstaged diff has
    /// both in working tree form, so line endings match and only real changes show: the index side from git, the
    /// other from disk. A side with no file costs nothing. The hunks are one more call, only when both sides exist.
    /// </summary>
    public async Task<FileDiff> ReadFileAsync(string worktree, FileDiffRequest request, CancellationToken ct)
    {
        var fullPath = WorktreeFile.Resolve(worktree, request.Path);
        if (request.OldPath is not null)
            WorktreeFile.Resolve(worktree, request.OldPath);
        var (oldSide, newSide) = request.Staged
            ? (Blob(worktree, request.HeadId, ct), Blob(worktree, request.IndexId, ct))
            : (CheckedOut(worktree, request, ct), WorktreeFile.ReadAsync(fullPath, ct));
        var hunks = HunksAsync(worktree, request, ct);
        await Task.WhenAll(oldSide, newSide, hunks).ConfigureAwait(false);
        var (oldContent, newContent) = (await oldSide.ConfigureAwait(false), await newSide.ConfigureAwait(false));
        return new FileDiff(oldContent, newContent, oldContent is null || newContent is null ? [] : await hunks.ConfigureAwait(false));
    }

    /// <summary>
    /// The hunks git sees. Only when both sides exist: from status for the old side, and for the new side of a staged
    /// diff. The new side of an unstaged diff is the disk, and a file deleted there gives no hunks above.
    /// </summary>
    private async Task<IReadOnlyList<Hunk>> HunksAsync(string worktree, FileDiffRequest request, CancellationToken ct)
    {
        var hasOld = request.Staged ? request.HeadId is not null : request.IndexId is not null;
        var hasNew = !request.Staged || request.IndexId is not null;
        if (!hasOld || !hasNew)
            return [];
        var diff = await new GitRunner(worktree, _runnerOptions).RunAsync(FilePatch.Arguments(new DiffTarget(request.Path, request.OldPath, request.Staged)), ct).ConfigureAwait(false);
        return FilePatch.Parse(diff.StandardOutputBytes).Hunks;
    }

    private async Task<FileContent?> Blob(string worktree, string? blobId, CancellationToken ct) =>
        blobId is null ? null : await _blobs.ReadAsync(worktree, blobId, ct).ConfigureAwait(false);

    private async Task<FileContent?> CheckedOut(string worktree, FileDiffRequest request, CancellationToken ct) =>
        request.IndexId is null ? null : await _blobs.ReadCheckedOutAsync(worktree, request.IndexId, request.Path, ct).ConfigureAwait(false);
}
