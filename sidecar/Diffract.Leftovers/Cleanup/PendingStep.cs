using Diffract.Git;

namespace Diffract.Leftovers.Cleanup;

/// <summary>A step ready to run, from a plan or, for an undo, from the log.</summary>
internal sealed record PendingStep
{
    /// <summary>Which step it is, with the outcome still to be set.</summary>
    public required StepResult Result { get; init; }

    /// <summary>The git command, or null for moving a folder to the Recycle Bin.</summary>
    public GitCommandLine? Command { get; init; }

    public IReadOnlyList<Expectation> Expects { get; init; } = [];

    public string? Folder { get; init; }

    /// <summary>The step in the plan, whose undo the log keeps once it is done. Null for an undo, which is not undone.</summary>
    public CleanupStep? Planned { get; init; }

    /// <summary>For an undo of a step nothing in git takes back: what to say instead, such as "Restore it from the Recycle Bin."</summary>
    public string? NotUndoable { get; init; }

    public StepResult As(StepOutcome outcome, string? reason) => Result with { Outcome = outcome, Reason = reason };
}
