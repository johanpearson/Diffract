namespace Diffract.Leftovers;

/// <summary>What makes a row something to act on: what to look at before removing it, and where to see it.</summary>
public sealed partial record Leftover
{
    /// <summary>What to look at before removing it, in plain English. Null when there is nothing to check.</summary>
    public string? Check { get; init; }

    /// <summary>
    /// The commit "Show in History" opens: a branch's or tag's commit, a worktree's HEAD, a stash, the newest
    /// checkpoint or orphaned commit. Null for a folder.
    /// </summary>
    public string? Commit { get; init; }
}
