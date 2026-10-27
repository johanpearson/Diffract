using Diffract.Git;
using Diffract.Git.History;
using Diffract.Git.Snapshot;
using Diffract.Leftovers;
using Diffract.Leftovers.Rules;

namespace Diffract.Sidecar;

/// <summary>The refs history shows besides branches, tags and worktree HEADs. Both are off by default.</summary>
internal sealed record HistoryShow(bool Checkpoints, bool Stashes);

/// <summary>What history shows of a snapshot: its refs, with agent hints, and the commits it starts from. Starts no git.</summary>
internal sealed class HistoryScope(RepoSnapshot snapshot, DiffractConfig config)
{
    public HistoryRefs Refs() => new(
        [.. Branches(), .. Tags(), .. Checkpoints(), .. Stashes()],
        [.. snapshot.Worktrees.Where(w => w.Head is not null && Attribution.ForWorktree(config, w) is not null).Select(w => w.Head!).Distinct(StringComparer.Ordinal)],
        [.. config.Agents.SelectMany(a => a.CommitSubjectPrefixes).Distinct(StringComparer.Ordinal)]);

    /// <summary>Branches, tags and worktree HEADs, plus checkpoints and stashes when they are shown. Each commit once.</summary>
    public IReadOnlyList<string> Tips(HistoryShow show) =>
    [
        .. HistoryReader.TipsOf(snapshot)
            .Concat(show.Checkpoints ? Checkpoints().Select(r => r.TargetId) : [])
            .Concat(show.Stashes ? Stashes().Select(r => r.TargetId) : [])
            .Distinct(StringComparer.Ordinal),
    ];

    private IEnumerable<HistoryRef> Branches() =>
        snapshot.Branches.Select(b => new HistoryRef(
            b.Name, b.IsRemote ? HistoryRefKind.RemoteBranch : HistoryRefKind.Branch, b.TargetId, BranchAgent(b)));

    /// <summary>A remote branch is matched without its remote's name, as the leftover rules do: "origin/claude/x" as "claude/x".</summary>
    private string? BranchAgent(BranchInfo branch) =>
        Attribution.ForBranch(config, branch.IsRemote ? branch.Name[(branch.Name.IndexOf('/') + 1)..] : branch.Name);

    private IEnumerable<HistoryRef> Tags() => snapshot.Tags.Select(t => new HistoryRef(t.Name, HistoryRefKind.Tag, t.TargetId, null));

    /// <summary>The refs outside branches, tags and the stash that match a checkpoint or agent ref pattern. Notes and the like stay hidden.</summary>
    private IEnumerable<HistoryRef> Checkpoints() =>
        from r in snapshot.OtherRefs
        let agent = Attribution.ForRef(config, r.FullName)
        where agent is not null || GlobMatcher.IsMatchAny(config.CheckpointRefPatterns, r.FullName, ignoreCase: false)
        select new HistoryRef(r.FullName.StartsWith("refs/", StringComparison.Ordinal) ? r.FullName[5..] : r.FullName, HistoryRefKind.Checkpoint, r.TargetId, agent);

    private IEnumerable<HistoryRef> Stashes() => snapshot.Stashes.Select(s => new HistoryRef(s.Name, HistoryRefKind.Stash, s.CommitId, null, s.Message));
}
