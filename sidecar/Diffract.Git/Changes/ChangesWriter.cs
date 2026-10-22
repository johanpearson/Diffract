using System.Text;

namespace Diffract.Git.Changes;

/// <summary>One hunk to stage or unstage, as <c>changes.file</c> showed it.</summary>
/// <param name="Path">The file's path in the worktree.</param>
/// <param name="OldPath">The path before a staged rename, so the staged diff is read as one. Null otherwise.</param>
/// <param name="HunkId">The <see cref="Hunk.Id"/> the user saw.</param>
public sealed record HunkRequest(string Path, string? OldPath, string HunkId);

/// <summary>
/// Stages and unstages whole files and single hunks in one worktree. Every write goes through a changes runner, which
/// runs nothing but <see cref="ChangesPolicy"/>. Nothing here throws a change away: whatever leaves the index stays on disk.
/// </summary>
public sealed class ChangesWriter
{
    private static readonly string[] StageFiles = ["add", "--pathspec-from-file=-", "--pathspec-file-nul"];
    private static readonly string[] UnstageFiles = ["restore", "--staged", "--pathspec-from-file=-", "--pathspec-file-nul"];
    private static readonly string[] UnstageFilesBeforeFirstCommit = ["rm", "--cached", "-q", "--pathspec-from-file=-", "--pathspec-file-nul"];

    private readonly string _worktree;
    private readonly GitRunner _changes;
    private readonly GitRunner _reads;
    private readonly IndexLockRetry _retry;

    /// <param name="worktree">The worktree's full path. Every change is made there and nowhere else.</param>
    public ChangesWriter(string worktree, GitRunnerOptions runnerOptions)
    {
        _worktree = worktree;
        _changes = new GitRunner(worktree, runnerOptions with { ChangesOnly = true });
        _reads = new GitRunner(worktree, runnerOptions with { ChangesOnly = false, ReadOnly = true });
        _retry = new IndexLockRetry(runnerOptions.TimeProvider);
    }

    /// <summary>Stages any number of files, new, changed or deleted, with one git call.</summary>
    public Task StageAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        WriteAsync(GitCommandLine.WithBytes(StageFiles, PathList(paths)), ct);

    /// <summary>
    /// Unstages any number of files with one git call, leaving them as they are on disk. Before the first commit
    /// there is no HEAD to restore from, so the files are taken out of the index instead. One read decides which.
    /// </summary>
    public async Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var input = PathList(paths);
        var args = await HasCommitAsync(ct).ConfigureAwait(false) ? UnstageFiles : UnstageFilesBeforeFirstCommit;
        await WriteAsync(GitCommandLine.WithBytes(args, input), ct).ConfigureAwait(false);
    }

    /// <summary>Stages one hunk of the unstaged diff, if it is still exactly as the user saw it. Two git calls.</summary>
    public Task StageHunkAsync(HunkRequest hunk, CancellationToken ct) => ApplyHunkAsync(hunk, HunkDirection.Stage, ct);

    /// <summary>Unstages one hunk of the staged diff, if it is still exactly as the user saw it. Two git calls.</summary>
    public Task UnstageHunkAsync(HunkRequest hunk, CancellationToken ct) => ApplyHunkAsync(hunk, HunkDirection.Unstage, ct);

    private async Task ApplyHunkAsync(HunkRequest hunk, HunkDirection direction, CancellationToken ct)
    {
        CheckPath(hunk.Path);
        if (hunk.OldPath is not null)
            WorktreeFile.Resolve(_worktree, hunk.OldPath);
        var diff = await _reads.RunAsync(FilePatch.Arguments(new DiffTarget(hunk.Path, hunk.OldPath, direction.FromStaged)), ct).ConfigureAwait(false);
        var patch = FilePatch.Parse(diff.StandardOutputBytes).PatchFor(hunk.HunkId) ?? throw new ChangedSinceShownException(hunk.Path);
        try
        {
            await WriteAsync(GitCommandLine.WithBytes(direction.Apply, patch), ct).ConfigureAwait(false);
        }
        catch (GitCommandException)
        {
            // The index changed between reading the diff and applying it. git apply changes nothing when it refuses.
            throw new ChangedSinceShownException(hunk.Path);
        }
    }

    private Task WriteAsync(GitCommandLine command, CancellationToken ct) => _retry.RunAsync(_changes, command, ct);

    private async Task<bool> HasCommitAsync(CancellationToken ct)
    {
        var result = await _reads.RunAsync(["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], [1], ct).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    /// <summary>The paths for <c>--pathspec-file-nul</c>, each checked first. None at all is refused, as git would stage nothing.</summary>
    private byte[] PathList(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            throw new ArgumentException("No paths to stage or unstage.", nameof(paths));
        foreach (var path in paths)
            CheckPath(path);
        return Encoding.UTF8.GetBytes(string.Concat(paths.Select(p => p + "\0")));
    }

    /// <summary>
    /// A path inside the worktree, and never a folder: status lists a folder only when it holds a repo of its own,
    /// and staging one would add it as a submodule.
    /// </summary>
    private void CheckPath(string path)
    {
        if (Directory.Exists(WorktreeFile.Resolve(_worktree, path)))
            throw new ArgumentException($"'{path}' is a folder with a repo of its own. Diffract never stages one.", nameof(path));
    }

    /// <summary>Which diff a hunk comes from, and how it is applied to the index.</summary>
    /// <param name="FromStaged">The hunk is from the staged diff.</param>
    private sealed record HunkDirection(bool FromStaged, string[] Apply)
    {
        public static readonly HunkDirection Stage = new(false, ["apply", "--cached", "--unidiff-zero", "-"]);

        public static readonly HunkDirection Unstage = new(true, ["apply", "--cached", "--unidiff-zero", "--reverse", "-"]);
    }
}
