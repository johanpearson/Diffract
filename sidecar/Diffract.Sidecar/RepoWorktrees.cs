using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar;

/// <summary>Turns a worktree named in a request into one the repo really has, so git only ever runs where the repo lives.</summary>
internal sealed class RepoWorktrees(RepoSessions repos)
{
    /// <summary>
    /// The worktree's path as the snapshot has it. A bare repo has no files, and neither has a worktree whose folder is
    /// gone, so both are refused like a folder that is not the repo's at all.
    /// </summary>
    public string Resolve(string? repoId, string? path)
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
