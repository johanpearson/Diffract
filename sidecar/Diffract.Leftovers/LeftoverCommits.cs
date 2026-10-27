using Diffract.Git;
using Diffract.Git.Parsing;
using Diffract.Git.Snapshot;
using Diffract.Leftovers.Rules;

namespace Diffract.Leftovers;

/// <summary>Commits as <c>git log</c> reads them: from these tips, leaving out every commit the excluded ones reach.</summary>
public sealed record CommitRange(IReadOnlyList<string> Tips, IReadOnlyList<string> Excluded);

/// <summary>What "show what is in it" lists: the newest commits only the leftover holds, and whether there are more.</summary>
public sealed record LeftoverContent(IReadOnlyList<CommitEntry> Commits, bool More);

/// <summary>
/// The commits only a leftover holds: what removing it would put at risk, the same rule as the snapshot's
/// commits-at-risk count. A branch, tag or worktree from its commit, a checkpoint session from its checkpoints, an
/// orphaned-commit row from its commits, a stash from itself; minus everything a branch, remote-tracking branch or
/// tag other than its own reaches. A folder holds no commits.
/// </summary>
public static class LeftoverCommits
{
    /// <summary>How many commits a row shows. More than that and it says there are more, and History has the rest.</summary>
    public const int Shown = 50;

    /// <summary>A stash is two or three commits; these are git's own bookkeeping, not work.</summary>
    private static readonly string[] StashParts = ["index on ", "untracked files on "];

    /// <summary>Starts no git: it reads the snapshot. Null for a folder, or a leftover the snapshot no longer has.</summary>
    public static CommitRange? RangeOf(RepoSnapshot repo, Leftover leftover, DiffractConfig config)
    {
        var tips = TipsOf(repo, leftover, config);
        return tips.Count == 0 ? null : new CommitRange(tips, OtherTips(repo, OwnRef(repo, leftover)));
    }

    /// <summary>Where the leftover's commits start: the commits of its row, its checkpoints, or its own commit.</summary>
    private static IReadOnlyList<string> TipsOf(RepoSnapshot repo, Leftover leftover, DiffractConfig config) => leftover.Kind switch
    {
        LeftoverKind.OrphanedCommit => OrphanedTips(repo, leftover, config),
        LeftoverKind.CheckpointSession => [.. repo.OtherRefs.Where(r => InSession(config, r, leftover.Name)).Select(r => r.TargetId)],
        LeftoverKind.LeftoverFolder => [],
        _ => leftover.Commit is { } commit ? [commit] : [],
    };

    private static IReadOnlyList<string> OrphanedTips(RepoSnapshot repo, Leftover leftover, DiffractConfig config) =>
        OrphanedCommitRule.Rows(repo, config).FirstOrDefault(r => r.Leftover.Name == leftover.Name && r.Leftover.Agent == leftover.Agent)
            .Commits?.Select(c => c.Id).ToList() ?? [];

    private static bool InSession(DiffractConfig config, RefInfo checkpoint, string session) =>
        CheckpointSessionRule.SessionOf(config, checkpoint.FullName)?.EndsWith($"/{session}", StringComparison.Ordinal) == true;

    /// <summary>The newest commits in the range, at most <see cref="Shown"/>, with one read-only <c>git log</c>.</summary>
    public static async Task<LeftoverContent> ReadAsync(GitRunner reads, LeftoverKind kind, CommitRange range, CancellationToken ct)
    {
        List<string> args = [.. CommitLogParser.Arguments];
        args.Insert(args.IndexOf("--stdin"), $"--max-count={Shown + 1}");
        IReadOnlyList<CommitEntry> commits = [];
        await reads.StreamAsync(new GitCommandLine(args, CommitLogParser.Input(range.Tips, range.Excluded)), async (output, token) =>
        {
            commits = await CommitLogParser.ReadAtMostAsync(output, Shown + 1, token).ConfigureAwait(false) ?? [];
            return true;
        }, ct).ConfigureAwait(false);

        if (kind == LeftoverKind.Stash)
            commits = [.. commits.Where(c => !StashParts.Any(p => c.Subject.StartsWith(p, StringComparison.Ordinal)))];
        return new LeftoverContent([.. commits.Take(Shown)], commits.Count > Shown);
    }

    /// <summary>The ref the leftover is, which must not count as "somewhere else": a branch, or a worktree's or tag's own.</summary>
    private static string? OwnRef(RepoSnapshot repo, Leftover leftover) => leftover.Kind switch
    {
        LeftoverKind.LocalBranch => $"refs/heads/{leftover.Name}",
        LeftoverKind.RemoteBranch => $"refs/remotes/{leftover.Name}",
        LeftoverKind.BackupTag => $"refs/tags/{leftover.Name}",
        LeftoverKind.Worktree or LeftoverKind.PrunableWorktree => repo.Worktrees.FirstOrDefault(w => w.Path == leftover.Path)?.Branch is { } branch
            ? $"refs/heads/{branch}"
            : null,
        _ => null,
    };

    /// <summary>Every branch, remote-tracking branch and tag but <paramref name="own"/>.</summary>
    private static IReadOnlyList<string> OtherTips(RepoSnapshot repo, string? own) =>
    [
        .. repo.Branches.Where(b => b.FullName != own).Select(b => b.TargetId)
            .Concat(repo.Tags.Where(t => t.FullName != own).Select(t => t.TargetId))
            .Where(ObjectId.IsValid)
            .Distinct(StringComparer.Ordinal),
    ];
}
