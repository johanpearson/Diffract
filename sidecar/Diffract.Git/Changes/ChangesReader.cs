using Diffract.Git.Files;
using Diffract.Git.Parsing;

namespace Diffract.Git.Changes;

/// <summary>Which diff of a changed file to read.</summary>
/// <param name="Path">The file's path in the worktree, from <see cref="ChangedFile.Path"/>.</param>
/// <param name="Staged">HEAD against the index when true, the index against the worktree when false.</param>
/// <param name="HeadId">From <see cref="ChangedFile.HeadId"/>.</param>
/// <param name="IndexId">From <see cref="ChangedFile.IndexId"/>.</param>
public sealed record FileDiffRequest(string Path, bool Staged, string? HeadId, string? IndexId);

/// <summary>The two sides of a file's diff. A side is null when the file does not exist there.</summary>
public sealed record FileDiff(FileContent? Old, FileContent? New);

/// <summary>
/// Reads what the Changes view shows, with as few git calls as possible: one status call per worktree, and at most
/// one call for each side of a file's diff, run side by side. Only reads.
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

    /// <summary>The worktree's branch and changed files, untracked files one by one. One git call.</summary>
    public async Task<WorktreeChanges> ReadStatusAsync(string worktree, CancellationToken ct)
    {
        var result = await new GitRunner(worktree, _runnerOptions).RunAsync(StatusParser.ChangesArguments, ct).ConfigureAwait(false);
        return WorktreeChanges.From(worktree, StatusParser.Parse(result.StandardOutput));
    }

    /// <summary>
    /// A staged diff has both sides in repo form, as history shows them: one git call per side. An unstaged diff has
    /// both in working tree form, so line endings match and only real changes show: the index side from git, the
    /// other from disk. A side with no file costs nothing.
    /// </summary>
    public async Task<FileDiff> ReadFileAsync(string worktree, FileDiffRequest request, CancellationToken ct)
    {
        var fullPath = WorktreeFile.Resolve(worktree, request.Path);
        var (oldSide, newSide) = request.Staged
            ? (Blob(worktree, request.HeadId, ct), Blob(worktree, request.IndexId, ct))
            : (CheckedOut(worktree, request, ct), WorktreeFile.ReadAsync(fullPath, ct));
        await Task.WhenAll(oldSide, newSide).ConfigureAwait(false);
        return new FileDiff(await oldSide.ConfigureAwait(false), await newSide.ConfigureAwait(false));
    }

    private async Task<FileContent?> Blob(string worktree, string? blobId, CancellationToken ct) =>
        blobId is null ? null : await _blobs.ReadAsync(worktree, blobId, ct).ConfigureAwait(false);

    private async Task<FileContent?> CheckedOut(string worktree, FileDiffRequest request, CancellationToken ct) =>
        request.IndexId is null ? null : await _blobs.ReadCheckedOutAsync(worktree, request.IndexId, request.Path, ct).ConfigureAwait(false);
}
