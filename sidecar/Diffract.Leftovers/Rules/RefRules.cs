using Diffract.Git;
using Diffract.Git.Snapshot;

namespace Diffract.Leftovers.Rules;

/// <summary>A local branch with no worktree that is merged, or whose upstream is gone after a squash merge.</summary>
public sealed class MergedBranchRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time) =>
        from b in Branches.WithoutWorktree(repo)
        where Branches.IsMergedOrGone(b)
        select Branches.Leftover(config, b, Verdict.SafeToRemove, b.IsMergedIntoMain
            ? $"Merged into {repo.MainBranch}"
            : $"Upstream branch was deleted, probably merged; {Describe.AtRisk(b.CommitsAtRisk ?? 0)}");
}

/// <summary>An unmerged local branch with no worktree and no commits for <c>staleAfterDays</c>.</summary>
public sealed class StaleBranchRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time)
    {
        var now = time.GetUtcNow();
        foreach (var b in Branches.WithoutWorktree(repo))
        {
            if (Branches.IsMergedOrGone(b) || b.LastCommitAt is not { } last || now - last < TimeSpan.FromDays(config.StaleAfterDays))
                continue;

            yield return Branches.Leftover(config, b, Verdict.NeedsReview,
                $"No commits for {Describe.Age(now - last)}, {Describe.AtRisk(b.CommitsAtRisk ?? 0)}");
        }
    }
}

/// <summary>A merged remote-tracking branch matching <c>remoteBranchPatterns</c>, still on the remote.</summary>
public sealed class MergedRemoteBranchRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time)
    {
        foreach (var b in repo.Branches)
        {
            var slash = b.Name.IndexOf('/', StringComparison.Ordinal);
            if (!b.IsRemote || !b.IsMergedIntoMain || slash < 0)
                continue;

            var (remote, name) = (b.Name[..slash], b.Name[(slash + 1)..]);
            if (name == repo.MainBranch || !GlobMatcher.IsMatchAny(config.RemoteBranchPatterns, name, ignoreCase: false))
                continue;

            yield return new(LeftoverKind.RemoteBranch, b.Name, null, Attribution.ForBranch(config, name),
                Verdict.SafeToRemove, 0, $"Merged into {repo.MainBranch}, still on {remote}");
        }
    }
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
                $"Stash looks made by a tool (\"{match}\"), {Describe.Age(now - s.CreatedAt)} old");
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
                Verdict.NeedsReview, 0, $"{Describe.Count(session.Count(), "checkpoint")}, newest is {Describe.Age(now - newest)} old");
        }
    }

    /// <summary>
    /// The pattern's fixed part plus the next segment, for example
    /// <c>refs/copilot/checkpoints/&lt;session&gt;</c> for <c>refs/copilot/checkpoints/**</c>.
    /// </summary>
    private static string? SessionOf(DiffractConfig config, string fullName)
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

/// <summary>A commit on no branch, tag or stash, found by <c>git fsck --unreachable</c>.</summary>
public sealed class OrphanedCommitRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time) =>
        from id in repo.UnreachableCommits
        select new Leftover(LeftoverKind.OrphanedCommit, id[..Math.Min(12, id.Length)], null, null,
            Verdict.NeedsReview, 1, "Commit is on no branch, tag or stash");
}

/// <summary>A backup tag under <c>archive.tagPrefix</c> older than <c>archive.reviewAfterDays</c>.</summary>
public sealed class OldBackupTagRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time)
    {
        var now = time.GetUtcNow();
        foreach (var t in repo.Tags)
        {
            if (!t.Name.StartsWith(config.Archive.TagPrefix, StringComparison.Ordinal) || t.CreatedAt is not { } created
                || now - created < TimeSpan.FromDays(config.Archive.ReviewAfterDays))
                continue;

            yield return new(LeftoverKind.BackupTag, t.Name, null, null, Verdict.NeedsReview, t.CommitsAtRisk ?? 0,
                $"Backup tag is {Describe.Age(now - created)} old, {Describe.AtRisk(t.CommitsAtRisk ?? 0)}");
        }
    }
}

/// <summary>A folder inside a worktree container that git does not know about, left by a failed worktree removal.</summary>
public sealed class LeftoverFolderRule : ILeftoverRule
{
    public IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time) =>
        from path in repo.LeftoverFolders
        select new Leftover(LeftoverKind.LeftoverFolder, Path.GetFileName(path), path, Attribution.ForPath(config, path),
            Verdict.NeedsReview, 0, "Folder sits with the worktrees, but git does not know it");
}
