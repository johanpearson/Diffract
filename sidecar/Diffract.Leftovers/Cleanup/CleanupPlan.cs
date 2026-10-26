using System.Text.Json.Serialization;
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

    /// <summary>
    /// Keeps several orphaned commits under one backup tag: the command makes a commit with all of them as parents,
    /// then <see cref="CleanupStep.Keep"/> tags it. Nothing is removed. One orphaned commit is a plain <see cref="Backup"/>.
    /// </summary>
    KeepCommits,
}

/// <summary>The backup tag a <see cref="StepKind.KeepCommits"/> step puts on the commit it made.</summary>
public sealed record KeepTag(string Name, string Message);

/// <summary>
/// What must still be true right before a step, or its undo, runs. When it is not, the step and the rest of its
/// leftover are skipped. Kept in the operation log with each undo, so it is stored by name.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RefAt), "refAt")]
[JsonDerivedType(typeof(RefAbsent), "refAbsent")]
[JsonDerivedType(typeof(RefPresent), "refPresent")]
[JsonDerivedType(typeof(TagOn), "tagOn")]
[JsonDerivedType(typeof(ObjectExists), "objectExists")]
[JsonDerivedType(typeof(WorktreeClean), "worktreeClean")]
[JsonDerivedType(typeof(NoFilesInUse), "noFilesInUse")]
[JsonDerivedType(typeof(PathAbsent), "pathAbsent")]
[JsonDerivedType(typeof(StashPresent), "stashPresent")]
[JsonDerivedType(typeof(StashAbsent), "stashAbsent")]
public abstract record Expectation
{
    /// <summary>A ref still holds this object.</summary>
    public sealed record RefAt(string FullName, string ObjectId) : Expectation;

    /// <summary>No ref has this name, so one can be made with it again.</summary>
    public sealed record RefAbsent(string FullName) : Expectation;

    /// <summary>The ref is still there, wherever it points, such as the tag "Keep these" made on a commit it made.</summary>
    public sealed record RefPresent(string FullName) : Expectation;

    /// <summary>A tag still points at this commit, such as a backup tag before its undo removes it.</summary>
    public sealed record TagOn(string FullName, string CommitId) : Expectation;

    /// <summary>Git still has the object, so a ref can point at it again.</summary>
    public sealed record ObjectExists(string ObjectId) : Expectation;

    /// <summary>The worktree has no changed or untracked files and is not locked.</summary>
    public sealed record WorktreeClean(string Path) : Expectation;

    /// <summary>No program has a file open in the folder: it can be renamed away and back. See docs/findings/cleanup.md.</summary>
    public sealed record NoFilesInUse(string Path) : Expectation;

    /// <summary>Nothing is at the path, so a worktree can be added there again.</summary>
    public sealed record PathAbsent(string Path) : Expectation;

    /// <summary>A stash with this commit is still in the list.</summary>
    public sealed record StashPresent(string CommitId) : Expectation;

    /// <summary>No stash in the list has this commit, so storing it again does not make a second one.</summary>
    public sealed record StashAbsent(string CommitId) : Expectation;
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

    /// <summary>What must be true right before <see cref="Undo"/> runs.</summary>
    public IReadOnlyList<Expectation> UndoExpects { get; init; } = [];

    /// <summary>Said in the dialog when undo has a limit, or is up to Windows rather than git.</summary>
    public string? UndoNote { get; init; }

    /// <summary>The dialog gives it its own checkbox, ticked, such as deleting a removed worktree's branch.</summary>
    public bool Optional { get; init; }

    /// <summary>The folder a <see cref="StepKind.RecycleFolder"/> step moves.</summary>
    public string? Folder { get; init; }

    /// <summary>The tag a <see cref="StepKind.KeepCommits"/> step puts on the commit its command makes.</summary>
    public KeepTag? Keep { get; init; }
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
