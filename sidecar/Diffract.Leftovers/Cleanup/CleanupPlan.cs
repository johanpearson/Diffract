using Diffract.Git;

namespace Diffract.Leftovers.Cleanup;

/// <summary>What a step does. The dialog groups and words steps by it; the executor runs each its own way.</summary>
public enum StepKind
{
    /// <summary>An annotated <c>archive/</c> tag on work that exists nowhere else, before anything is removed.</summary>
    Backup,
    RemoveWorktree,
    DeleteBranch,
    DeleteRemoteBranch,

    /// <summary>
    /// Drops a stash. The command names the stash's place when the plan was made; the executor finds its place again
    /// by its commit right before, since git takes no commit there and agents shift the list.
    /// </summary>
    DropStash,

    /// <summary>Deletes refs, such as a checkpoint session's, in one all-or-nothing <c>update-ref --stdin</c>.</summary>
    DeleteRefs,
    DeleteTag,

    /// <summary>Moves a folder to the Windows Recycle Bin. Not git, so the step has no command.</summary>
    RecycleFolder,
}

/// <summary>What must still be true right before a step runs. When it is not, the step and the rest of its leftover are skipped.</summary>
public abstract record Expectation
{
    /// <summary>A ref still holds this object.</summary>
    public sealed record RefAt(string FullName, string ObjectId) : Expectation;

    /// <summary>The worktree has no changed or untracked files and is not locked.</summary>
    public sealed record WorktreeClean(string Path) : Expectation;

    /// <summary>No program has a file open in the folder: it can be renamed away and back. See docs/findings/cleanup.md.</summary>
    public sealed record NoFilesInUse(string Path) : Expectation;

    /// <summary>A stash with this commit is still in the list.</summary>
    public sealed record StashPresent(string CommitId) : Expectation;
}

/// <summary>One step of a cleanup: what it does, what must still be true first, and what takes it back.</summary>
public sealed record CleanupStep
{
    public required StepKind Kind { get; init; }

    /// <summary>The step in plain English, as the dialog lists it.</summary>
    public required string Description { get; init; }

    /// <summary>The git command, or null for a step that is not git.</summary>
    public GitCommandLine? Command { get; init; }

    public IReadOnlyList<Expectation> Expects { get; init; } = [];

    /// <summary>What takes it back, or null when nothing in git can.</summary>
    public GitCommandLine? Undo { get; init; }

    /// <summary>Said in the dialog when undo has a limit, or is up to Windows rather than git.</summary>
    public string? UndoNote { get; init; }

    /// <summary>The dialog gives it its own checkbox, ticked, such as deleting a removed worktree's branch.</summary>
    public bool Optional { get; init; }

    /// <summary>The folder a <see cref="StepKind.RecycleFolder"/> step moves.</summary>
    public string? Folder { get; init; }
}

/// <summary>The steps for one leftover, in order, or why there are none.</summary>
public sealed record LeftoverPlan(Leftover Leftover, IReadOnlyList<CleanupStep> Steps)
{
    /// <summary>Why nothing is planned, such as "In use". Null when there are steps.</summary>
    public string? Refused { get; init; }

    public static LeftoverPlan Refuse(Leftover leftover, string why) => new(leftover, []) { Refused = why };
}

/// <summary>
/// Everything a cleanup will do, in order. The confirm dialog shows it, and applying runs it and nothing else, so the
/// two can never disagree.
/// </summary>
public sealed record CleanupPlan(IReadOnlyList<LeftoverPlan> Leftovers);
