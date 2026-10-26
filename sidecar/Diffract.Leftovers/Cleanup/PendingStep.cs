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

    /// <summary>
    /// Why it is not run on purpose: the user left it out, or, for an undo, nothing in git takes it back ("Restore it
    /// from the Recycle Bin."). Unlike a skip, it does not stop the rest of its leftover.
    /// </summary>
    public string? NotRun { get; init; }

    public StepResult As(StepOutcome outcome, string? reason) => Result with { Outcome = outcome, Reason = reason };
}
