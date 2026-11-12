using System.Globalization;
using Diffract.Git;
using Diffract.Git.Refs;
using Diffract.Git.Snapshot;

namespace Diffract.Leftovers.Cleanup;

/// <summary>One branch on one remote, such as "origin" and "fix/x".</summary>
public sealed record RemoteTarget(string Remote, string Branch)
{
    /// <summary>Diffract's copy of it, such as "origin/fix/x".</summary>
    public string TrackingName => $"{Remote}/{Branch}";
}

/// <summary>What the remote itself said just now: its branches with the commit each is at, and which one is its default.</summary>
/// <param name="DefaultBranch">The branch its HEAD points at, such as "main". Null when it names none.</param>
/// <param name="CommitsAtRisk">
/// Commits only the branch's copy here has, counted for this one branch, since snapshots skip remote-tracking
/// branches. Null when it was not counted.
/// </param>
public sealed record RemoteAnswer(string Remote, IReadOnlyDictionary<string, string> Heads, string? DefaultBranch, int? CommitsAtRisk = null);

/// <summary>
/// Plan, then apply, for "Delete remote branch": one branch on a remote, for everyone, as the remote itself has it now.
/// It is planned like any cleanup, so it gets the same backup tag for work that exists nowhere else, the same confirm
/// dialog and the same undo. A pure function over the snapshot and what the remote just reported.
/// </summary>
public static class RemoteBranchPlanner
{
    /// <summary>
    /// Asks the remote what it has now, and counts the commits only Diffract's copy of the branch holds. Reads only: the
    /// network for the first, and one <c>rev-list</c> for the second. Throws when the remote cannot be reached.
    /// </summary>
    public static async Task<RemoteAnswer> ReadAsync(RepoSnapshot repo, GitRunnerOptions options, RemoteTarget target, CancellationToken ct)
    {
        var network = RemoteHeads.RunnerFor(repo.RepoRoot, options);
        var heads = await RemoteHeads.ReadWithIdsAsync(network, target.Remote, ct).ConfigureAwait(false);
        var defaultBranch = await RemoteHeads.ReadDefaultAsync(network, target.Remote, ct).ConfigureAwait(false);
        var answer = new RemoteAnswer(target.Remote, heads, defaultBranch);
        return Uncounted(repo, answer, target) is { } copy ? answer with { CommitsAtRisk = await CountAsync(repo, options, copy, ct).ConfigureAwait(false) } : answer;
    }

    /// <summary>Diffract's copy when it is at the commit the remote has but its commits at risk were never counted.</summary>
    private static BranchInfo? Uncounted(RepoSnapshot repo, RemoteAnswer answer, RemoteTarget target) =>
        repo.Branches.FirstOrDefault(b => b.IsRemote && b.Name == target.TrackingName) is { CommitsAtRisk: null } copy
        && answer.Heads.GetValueOrDefault(target.Branch) == copy.TargetId
            ? copy
            : null;

    private static async Task<int> CountAsync(RepoSnapshot repo, GitRunnerOptions options, BranchInfo copy, CancellationToken ct)
    {
        var reads = new GitRunner(repo.RepoRoot, options with { ReadOnly = true, ChangesOnly = false, CleanupOnly = false });
        var counted = await reads.RunAsync(RepoSnapshotBuilder.CommitsAtRiskArguments(copy.FullName), ct).ConfigureAwait(false);
        return int.Parse(counted.StandardOutput.Trim(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The plan for <paramref name="branch"/> on the remote. Refused, with the reason, when it is the remote's default
    /// branch or Diffract cannot tell which is; when the remote no longer has it; when Diffract's copy is not at the
    /// commit the remote has, since an undo needs the commit here; or when the commits at risk are unknown.
    /// </summary>
    public static CleanupPlan Plan(RepoSnapshot repo, RemoteAnswer remote, string branch, TimeProvider time)
    {
        var name = $"{remote.Remote}/{branch}";
        var copy = repo.Branches.FirstOrDefault(b => b.IsRemote && b.Name == name);
        var atRisk = copy?.CommitsAtRisk ?? remote.CommitsAtRisk;
        var leftover = new Leftover(LeftoverKind.RemoteBranch, name, null, null, Verdict.NeedsReview, atRisk, "Picked in the history.") { Commit = copy?.TargetId };
        if (Refusal(remote, branch, copy, atRisk) is { } why)
            return new CleanupPlan([LeftoverPlan.Refuse(leftover, why)]);
        var steps = RefSteps.DeletedOnRemote(new RemoteTarget(remote.Remote, branch), remote.Heads[branch], atRisk!.Value, new BackupTags(repo, time.GetUtcNow()));
        return new CleanupPlan([new LeftoverPlan(leftover, steps)]);
    }

    private static string? Refusal(RemoteAnswer remote, string branch, BranchInfo? copy, int? atRisk)
    {
        if (remote.DefaultBranch is null)
            return $"Diffract could not tell which branch is the default of {remote.Remote}, and never deletes that one.";
        if (remote.DefaultBranch == branch)
            return $"{branch} is the default branch of {remote.Remote}. Diffract does not delete it.";
        if (!remote.Heads.TryGetValue(branch, out var commit))
            return $"{remote.Remote} does not have {branch} any more. Fetch with prune to tidy up.";
        if (copy is null || copy.TargetId != commit)
            return $"Diffract's copy of {remote.Remote}/{branch} is not at the commit {remote.Remote} has. Fetch first, so that the delete can be undone.";
        return atRisk is null ? "Diffract could not tell whether it holds work that exists nowhere else." : null;
    }
}
