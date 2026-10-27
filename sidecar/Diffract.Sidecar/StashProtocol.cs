using Diffract.Git.Changes;

namespace Diffract.Sidecar;

// Params and results of the stash methods, as in Protocol.cs. They mirror ui/src/protocol.ts.

/// <summary>The params of <c>stash.push</c>.</summary>
internal sealed record StashPushParams
{
    public string? RepoId { get; init; }

    public string? Worktree { get; init; }

    /// <summary>From the commit box. Only its first line is used. Empty or missing: git names it "WIP on &lt;branch&gt;".</summary>
    public string? Message { get; init; }

    /// <summary>Stash new files too.</summary>
    public bool IncludeUntracked { get; init; }
}

/// <summary>The params of <c>stash.apply</c>.</summary>
internal sealed record StashApplyParams(string? RepoId, string? Worktree, string? Commit);

/// <summary>The result of <c>stash.push</c>.</summary>
/// <param name="CommitId">The new stash's commit.</param>
/// <param name="Message">What happened, in plain words.</param>
/// <param name="Status">The worktree afterwards.</param>
internal sealed record StashPushResult(string CommitId, string Message, WorktreeChanges Status);

/// <summary>The result of <c>stash.apply</c>.</summary>
/// <param name="Conflicts">The stash applied with conflicts. It is kept, and <see cref="Status"/> lists the conflicted files.</param>
internal sealed record StashApplyResult(bool Conflicts, string Message, WorktreeChanges Status);

/// <summary>The params of <c>cleanup.planStash</c>: the stash to drop, by its commit, since its place in the list shifts.</summary>
internal sealed record CleanupPlanStashParams(string? RepoId, string? Commit);
