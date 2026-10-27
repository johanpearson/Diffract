using Diffract.Git.Snapshot;

namespace Diffract.Leftovers;

/// <summary>Ordered from least to most cautious, so the classifier can keep the highest.</summary>
public enum Verdict { SafeToRemove, NeedsReview, InUse }

public enum LeftoverKind
{
    Worktree, PrunableWorktree, LocalBranch, RemoteBranch,
    Stash, CheckpointSession, OrphanedCommit, BackupTag, LeftoverFolder,
}

/// <param name="Agent">The agent the config patterns point to. A hint only: it never changes the verdict.</param>
/// <param name="CommitsAtRisk">
/// Commits that exist nowhere else and would be lost on removal without a backup.
/// Null when they could not be counted, or when the item holds no commits, as a leftover folder.
/// </param>
/// <param name="Reason">One plain-English sentence.</param>
public sealed record Leftover(
    LeftoverKind Kind,
    string Name,
    string? Path,
    string? Agent,
    Verdict Verdict,
    int? CommitsAtRisk,
    string Reason)
{
    /// <summary>What to look at before removing it, in plain English. Null when there is nothing to check.</summary>
    public string? Check { get; init; }

    /// <summary>
    /// The commit "Show in History" opens: a branch's or tag's commit, a worktree's HEAD, a stash, the newest
    /// checkpoint or orphaned commit. Null for a folder.
    /// </summary>
    public string? Commit { get; init; }
}

/// <summary>A pure check over a snapshot: no git calls, no file system.</summary>
public interface ILeftoverRule
{
    IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time);
}
