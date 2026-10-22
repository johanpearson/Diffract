using System.Text.Json;
using Diffract.Git;
using Diffract.Git.Changes;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar;

/// <summary>
/// <c>changes.status</c> and <c>changes.file</c>: what the Changes view shows of one worktree. They only read. The
/// worktree must be one in the repo's snapshot, so git only ever runs where the repo lives.
/// </summary>
internal sealed class ChangesMethods(RepoSessions repos, GitRunnerOptions runnerOptions)
{
    private readonly ChangesReader _reader = new(runnerOptions);

    /// <summary>The worktree's branch and changed files. One git call.</summary>
    public async Task<JsonElement> StatusAsync(JsonElement? parameters, CancellationToken ct)
    {
        var status = SidecarMethods.Params<ChangesStatusParams>(parameters);
        var worktree = WorktreeOf(status.RepoId, status.Worktree);
        return SidecarMethods.Result(await _reader.ReadStatusAsync(worktree, ct).ConfigureAwait(false));
    }

    /// <summary>The two sides of one file's diff, staged or not. At most one git call per side, run side by side.</summary>
    public async Task<JsonElement> FileAsync(JsonElement? parameters, CancellationToken ct)
    {
        var file = SidecarMethods.Params<ChangesFileParams>(parameters);
        var worktree = WorktreeOf(file.RepoId, file.Worktree);
        var request = new FileDiffRequest(SidecarMethods.Required(file.Path, "path"), file.Staged, file.HeadId, file.IndexId);
        try
        {
            return SidecarMethods.Result(ChangesFile.From(await _reader.ReadFileAsync(worktree, request, ct).ConfigureAwait(false)));
        }
        catch (ArgumentException e)
        {
            // A path outside the worktree, or an id that is not one.
            throw new RpcException(RpcErrorCodes.InvalidParams, e.Message);
        }
    }

    /// <summary>
    /// The worktree's path as the snapshot has it. A bare repo has no files, and neither has a worktree whose folder is
    /// gone, so both are refused like a folder that is not the repo's at all.
    /// </summary>
    private string WorktreeOf(string? repoId, string? path)
    {
        var snapshot = repos.SnapshotOf(SidecarMethods.Required(repoId, "repoId"));
        var wanted = SidecarMethods.Required(path, "worktree");
        var worktree = Path.IsPathFullyQualified(wanted)
            ? snapshot.Worktrees.FirstOrDefault(w => SamePath(w.Path, wanted))
            : null;
        return worktree is { IsBare: false, IsPrunable: false }
            ? worktree.Path
            : throw new RpcException(RpcErrorCodes.InvalidParams, $"Not a worktree of this repo with files to show: {wanted}");
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
