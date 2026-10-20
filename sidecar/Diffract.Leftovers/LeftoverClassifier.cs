using Diffract.Git;
using Diffract.Git.Snapshot;
using Diffract.Leftovers.Rules;

namespace Diffract.Leftovers;

/// <summary>
/// Runs every rule and resolves overlaps: an item reported by several rules appears once,
/// with the most cautious verdict. Results are sorted by verdict, kind and name.
/// </summary>
public sealed class LeftoverClassifier(IReadOnlyList<ILeftoverRule> rules)
{
    /// <summary>In order of preference when two rules give the same verdict: the first one's reason wins.</summary>
    public static IReadOnlyList<ILeftoverRule> DefaultRules { get; } =
    [
        new RecentActivityRule(),
        new LockedWorktreeRule(),
        new DirtyWorktreeRule(),
        new PrunableWorktreeRule(),
        new MergedWorktreeRule(),
        new StaleWorktreeRule(),
        new DetachedWorktreeRule(),
        new MergedBranchRule(),
        new StaleBranchRule(),
        new MergedRemoteBranchRule(),
        new ToolStashRule(),
        new CheckpointSessionRule(),
        new OrphanedCommitRule(),
        new OldBackupTagRule(),
        new LeftoverFolderRule(),
    ];

    public LeftoverClassifier() : this(DefaultRules)
    {
    }

    /// <param name="runningFrom">The folder Diffract runs from. The worktree that contains it is never a leftover.</param>
    public IReadOnlyList<Leftover> Classify(RepoSnapshot repo, DiffractConfig config, TimeProvider time, string? runningFrom = null)
    {
        var kept = new Dictionary<(LeftoverKind, string), Leftover>();
        foreach (var rule in rules)
        {
            foreach (var leftover in rule.Evaluate(repo, config, time))
            {
                if (IsExcluded(leftover, repo, config, runningFrom))
                    continue;

                var key = (leftover.Kind, leftover.Path ?? leftover.Name);
                if (!kept.TryGetValue(key, out var existing) || leftover.Verdict > existing.Verdict)
                    kept[key] = leftover;
            }
        }

        return [.. kept.Values.OrderBy(l => l.Verdict).ThenBy(l => l.Kind).ThenBy(l => l.Name, StringComparer.Ordinal)];
    }

    private static bool IsExcluded(Leftover leftover, RepoSnapshot repo, DiffractConfig config, string? runningFrom)
    {
        if (leftover.Path is not { } path)
            return false;
        if (runningFrom is not null && IsSameOrInside(runningFrom, path))
            return true;
        if (leftover.Kind is not (LeftoverKind.Worktree or LeftoverKind.PrunableWorktree))
            return false;

        var worktree = repo.Worktrees.FirstOrDefault(w => SamePath(w.Path, path));
        if (worktree is null || worktree.IsMain)
            return worktree is not null;

        return GlobMatcher.IsMatchAny(config.HiddenWorktreePathPatterns, path, ignoreCase: true) && !(worktree.DirtyFileCount > 0);
    }

    private static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrInside(string child, string parent)
    {
        var (c, p) = (Normalize(child), Normalize(parent));
        return c.Equals(p, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
