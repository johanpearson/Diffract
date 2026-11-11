using Diffract.Git.Snapshot;

namespace Diffract.Leftovers.Cleanup;

/// <summary>
/// Plan, then apply, for "Prune remote branches": the remote-tracking branches of one remote that the remote no longer
/// has. A pure function over the snapshot and the branch names the remote just reported. The plan has one leftover per
/// branch, so the dialog lists them like any other cleanup.
/// </summary>
public static class PrunePlanner
{
    /// <summary>
    /// The plan for <paramref name="remote"/>. A remote that reports no branches at all plans nothing, since it is more
    /// likely to be misbehaving than to have lost every branch.
    /// </summary>
    public static CleanupPlan Plan(RepoSnapshot repo, string remote, IReadOnlySet<string> remoteBranches) =>
        new([.. StaleBranches(repo, remote, remoteBranches).Select(Planned)]);

    public static IReadOnlyList<BranchInfo> StaleBranches(RepoSnapshot repo, string remote, IReadOnlySet<string> remoteBranches)
    {
        if (remoteBranches.Count == 0)
            return [];
        var prefix = $"refs/remotes/{remote}/";
        return
        [
            .. repo.Branches.Where(b => b.IsRemote && b.FullName.StartsWith(prefix, StringComparison.Ordinal)
                && b.FullName[prefix.Length..] is var name && name != "HEAD" && !remoteBranches.Contains(name)),
        ];
    }

    private static LeftoverPlan Planned(BranchInfo branch) => new(
        new Leftover(LeftoverKind.RemoteBranch, branch.Name, null, null, Verdict.SafeToRemove, null, "The remote no longer has this branch."),
        RefSteps.PrunedRemoteBranch(branch));
}
