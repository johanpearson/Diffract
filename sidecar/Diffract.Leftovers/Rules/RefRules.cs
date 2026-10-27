using System.Text.RegularExpressions;
using Diffract.Git;
using Diffract.Git.Snapshot;

namespace Diffract.Leftovers.Rules;

/// <summary>A local branch with no worktree that is merged, or whose upstream is gone after a squash merge.</summary>
public sealed class MergedBranchRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time) =>
        from b in Branches.WithoutWorktree(repo)
        where Branches.IsMergedOrGone(b)
        select Branches.Leftover(config, b, b.IsMergedIntoMain
            ? new Finding(Verdict.SafeToRemove, $"Merged into {repo.MainBranch}")
            : new Finding(Verdict.SafeToRemove, $"Upstream branch was deleted, probably merged; {Describe.AtRisk(b.CommitsAtRisk ?? 0)}",
                Describe.SquashMerged(b.CommitsAtRisk ?? 0, repo.MainBranch)));
}

/// <summary>An unmerged local branch with no worktree and no commits for <c>staleAfterDays</c>.</summary>
public sealed class StaleBranchRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time)
    {
        var now = time.GetUtcNow();
        return
            from b in Branches.WithoutWorktree(repo)
            where !Branches.IsMergedOrGone(b)
            let idle = Branches.Idle(b, now)
            where idle >= TimeSpan.FromDays(config.StaleAfterDays)
            select Branches.Leftover(config, b, new Finding(Verdict.NeedsReview,
                $"No commits for {Describe.Age(idle.Value)}, {Describe.AtRisk(b.CommitsAtRisk ?? 0)}",
                Describe.LookAtCommits(b.CommitsAtRisk ?? 0, "on this branch", "Deleting it")));
    }
}

/// <summary>A merged remote-tracking branch matching <c>remoteBranchPatterns</c>, still on the remote.</summary>
public sealed class MergedRemoteBranchRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time) =>
        from b in repo.Branches
        where b.IsRemote && b.IsMergedIntoMain
        let slash = b.Name.IndexOf('/', StringComparison.Ordinal)
        where slash >= 0
        let remote = b.Name[..slash]
        let name = b.Name[(slash + 1)..]
        where name != repo.MainBranch && GlobMatcher.IsMatchAny(config.RemoteBranchPatterns, name, ignoreCase: false)
        select new Leftover(LeftoverKind.RemoteBranch, b.Name, null, Attribution.ForBranch(config, name),
            Verdict.SafeToRemove, 0, $"Merged into {repo.MainBranch}, still on {remote}") { Commit = b.TargetId };
}

/// <summary>A stash whose message points to a tool, such as a rebase autostash.</summary>
public sealed class ToolStashRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time)
    {
        var now = time.GetUtcNow();
        foreach (var s in repo.Stashes)
        {
            if (config.ToolStashPatterns.FirstOrDefault(p => s.Message.Contains(p, StringComparison.OrdinalIgnoreCase)) is not { } match)
                continue;

            yield return new(LeftoverKind.Stash, s.Name, null, null, Verdict.NeedsReview, 1,
                $"Stash looks made by a tool (\"{match}\"), {Describe.Age(now - s.CreatedAt)} old")
            {
                Check = "Look at its changes in History. Dropping it keeps them under a backup tag.",
                Commit = s.CommitId,
            };
        }
    }
}

/// <summary>
/// Checkpoint refs grouped by session: everything under the same <c>&lt;session-id&gt;</c> is one leftover,
/// reported once its newest checkpoint is <c>staleAfterDays</c> old.
/// </summary>
public sealed class CheckpointSessionRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time)
    {
        var now = time.GetUtcNow();
        var sessions = repo.OtherRefs
            .Select(r => (Ref: r, Session: SessionOf(config, r.FullName)))
            .Where(x => x.Session is not null)
            .GroupBy(x => x.Session!, StringComparer.Ordinal);

        foreach (var session in sessions)
        {
            if (session.Max(x => x.Ref.CreatedAt) is not { } newest || now - newest < TimeSpan.FromDays(config.StaleAfterDays))
                continue;

            var name = session.Key[(session.Key.LastIndexOf('/') + 1)..];
            yield return new(LeftoverKind.CheckpointSession, name, null, Attribution.ForRef(config, session.First().Ref.FullName),
                Verdict.NeedsReview, 0, $"{Describe.Count(session.Count(), "checkpoint")}, newest is {Describe.Age(now - newest)} old")
            {
                Check = "Snapshots an agent took while it worked. Look at the newest in History, and remove them once that work is committed.",
                Commit = session.MaxBy(x => x.Ref.CreatedAt).Ref.TargetId,
            };
        }
    }

    /// <summary>
    /// The pattern's fixed part plus the next segment, for example
    /// <c>refs/copilot/checkpoints/&lt;session&gt;</c> for <c>refs/copilot/checkpoints/**</c>.
    /// </summary>
    internal static string? SessionOf(DiffractConfig config, string fullName)
    {
        foreach (var pattern in config.CheckpointRefPatterns)
        {
            if (!GlobMatcher.IsMatch(pattern, fullName, ignoreCase: false))
                continue;

            var fixedCount = pattern.Split('/').TakeWhile(s => !s.Contains('*') && !s.Contains('?')).Count();
            var segments = fullName.Split('/');
            if (segments.Length > fixedCount + 1)
                return string.Join('/', segments.Take(fixedCount + 1));
        }

        return null;
    }
}

/// <summary>
/// Commits that no ref and no reflog points to, found by <c>git fsck --unreachable</c>. Checkpoint commits an
/// agent tool dropped itself (<c>checkpointCommitPrefixes</c>) are "Safe"; the rest need review. A commit an agent
/// made, or a checkpoint, is grouped with the others of its session, by the session id in its subject; any other
/// commit is a row of its own, so each row is something to act on. Commits still in a reflog, such as the
/// originals of squash-merged branches, are not reported.
/// </summary>
public sealed partial class OrphanedCommitRule : ILeftoverRule
{
    /// <summary>
    /// The subject of the commit "Keep these" makes to hold several orphaned commits under one tag. Once that tag is
    /// removed it is unreachable itself, but it is Diffract's own and holds nothing, so it is never a row.
    /// </summary>
    public const string HolderSubject = "Diffract kept these commits";

    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time) =>
        Rows(repo, config).Select(row => row.Leftover);

    /// <summary>Each row with the commits in it, so "Keep these" keeps exactly what the row counts.</summary>
    public static IEnumerable<(Leftover Leftover, IReadOnlyList<UnreachableCommit> Commits)> Rows(RepoSnapshot repo, DiffractConfig config) =>
        from commit in repo.UnreachableCommits
        where !commit.Subject.StartsWith(HolderSubject, StringComparison.Ordinal)
        let key = RowKey.Of(config, commit)
        group commit by key into rowGroup
        let commits = (IReadOnlyList<UnreachableCommit>)[.. rowGroup.OrderByDescending(c => c.CommittedAt)]
        select (rowGroup.Key.Row(commits), commits);

    /// <summary>What puts commits in the same row: their session, or, without one, the commit itself.</summary>
    private sealed record RowKey
    {
        public required bool Checkpoint { get; init; }

        public string? Agent { get; init; }

        /// <summary>The agent's session the commit belongs to, from its subject. Null for a commit of its own.</summary>
        public string? Session { get; init; }

        /// <summary>The commit, for a row of its own. Null for a session's row.</summary>
        public string? Commit { get; init; }

        public static RowKey Of(DiffractConfig config, UnreachableCommit commit)
        {
            var checkpoint = config.CheckpointCommitPrefixes.Any(p => commit.Subject.StartsWith(p, StringComparison.Ordinal));
            var agent = Attribution.ForCommit(config, commit);
            var session = checkpoint || agent is not null ? SessionId().Match(commit.Subject) is { Success: true } m ? m.Value : null : null;
            return new RowKey { Checkpoint = checkpoint, Agent = agent, Session = session, Commit = session is null ? commit.Id : null };
        }

        /// <param name="commits">Newest first.</param>
        public Leftover Row(IReadOnlyList<UnreachableCommit> commits) => (Session is null
            ? new Leftover(LeftoverKind.OrphanedCommit, $"{commits[0].Id[..Math.Min(8, commits[0].Id.Length)]} {Subject(commits[0])}", null, Agent, VerdictOf, 1, Reason(1))
            {
                Check = "Look at what it holds, then keep it, or let git delete it.",
            }
            : new Leftover(LeftoverKind.OrphanedCommit, $"{Describe.Count(commits.Count, Checkpoint ? "checkpoint commit" : "commit")} of session {Session[..8]}",
                null, Agent, VerdictOf, commits.Count, Reason(commits.Count))
            {
                Check = Checkpoint
                    ? "Snapshots an agent tool took and then dropped. Keep them only if you want that work back."
                    : "An agent's session left these. Look at what they hold, then keep them, or let git delete them.",
            }) with { Commit = commits[0].Id };

        private Verdict VerdictOf => Checkpoint ? Verdict.SafeToRemove : Verdict.NeedsReview;

        private string Reason(int count)
        {
            var them = count == 1 ? "it" : "them";
            return Checkpoint
                ? $"Checkpoints an agent tool made and then dropped; git may delete {them} in its next cleanup"
                : $"Not on any branch, tag, stash or reflog, so git may delete {them} in its next cleanup";
        }

        private static string Subject(UnreachableCommit commit) => commit.Subject.Length > 0 ? commit.Subject : "(no subject)";
    }

    /// <summary>A session id as agents write them in their subjects: a GUID.</summary>
    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex SessionId();
}

/// <summary>A backup tag under <c>archive.tagPrefix</c> older than <c>archive.reviewAfterDays</c>.</summary>
public sealed class OldBackupTagRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time)
    {
        var now = time.GetUtcNow();
        return
            from t in repo.Tags
            where t.Name.StartsWith(config.Archive.TagPrefix, StringComparison.Ordinal)
            let age = now - t.CreatedAt
            where age >= TimeSpan.FromDays(config.Archive.ReviewAfterDays)
            select new Leftover(LeftoverKind.BackupTag, t.Name, null, null, Verdict.NeedsReview, t.CommitsAtRisk,
                $"Backup tag is {Describe.Age(age.Value)} old, {Describe.AtRisk(t.CommitsAtRisk ?? 0)}")
            {
                Check = t.CommitsAtRisk is > 0 and var kept
                    ? $"Look at the {Describe.Count(kept, "commit")} it kept in History. Delete it once you no longer need that work."
                    : "Its commits are on a branch or another tag too, so deleting it loses nothing.",
                Commit = t.TargetId,
            };
    }
}

/// <summary>
/// A folder git does not know, left by a worktree removal that did not finish: "Safe" when it holds no files,
/// otherwise it needs review.
/// </summary>
public sealed class LeftoverFolderRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time) =>
        from folder in repo.LeftoverFolders
        let verdict = folder.HoldsNoFiles ? Verdict.SafeToRemove : Verdict.NeedsReview
        let reason = folder.WasWorktree ? "Was a worktree of this repo, but git no longer knows it"
            : folder.HoldsNoFiles ? "Empty folder left where a worktree was"
            : "Folder sits with the worktrees, but git does not know it"
        select new Leftover(LeftoverKind.LeftoverFolder, Path.GetFileName(folder.Path), folder.Path,
            Attribution.ForPath(config, folder.Path), verdict, null, reason)
        {
            Check = folder.HoldsNoFiles ? null : "Open the folder and look for files you want to keep. Cleaning it up moves it to the Recycle Bin.",
        };
}
