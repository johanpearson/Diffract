namespace Diffract.Git.Files;

/// <summary>Reads one blob with <c>cat-file</c>, one git call each, stopping git early for binary and large files.</summary>
public sealed class BlobReader(GitRunnerOptions runnerOptions)
{
    /// <summary>The stored bytes. No filter or textconv from the repo's config ever runs.</summary>
    public Task<FileContent> ReadAsync(string directory, string blobId, CancellationToken ct, int maxBytes = FileContentReader.MaxFileBytes) =>
        ReadAsync(directory, ["cat-file", "blob", ObjectId.Require(blobId)], maxBytes, ct);

    /// <summary>
    /// The blob as git would write it to <paramref name="path"/> in the worktree: with the line endings and filters
    /// that apply there. It then matches the file on disk byte for byte when nothing changed.
    /// </summary>
    public Task<FileContent> ReadCheckedOutAsync(string worktree, string blobId, string path, CancellationToken ct) =>
        ReadAsync(worktree, ["cat-file", "--filters", "--path=" + path, ObjectId.Require(blobId)], FileContentReader.MaxFileBytes, ct);

    private async Task<FileContent> ReadAsync(string directory, IReadOnlyList<string> args, int maxBytes, CancellationToken ct)
    {
        using var reader = new FileContentReader(maxBytes);
        await new GitRunner(directory, runnerOptions).StreamAsync(args, reader.ReadAsync, [], ct).ConfigureAwait(false);
        return reader.Content;
    }
}
