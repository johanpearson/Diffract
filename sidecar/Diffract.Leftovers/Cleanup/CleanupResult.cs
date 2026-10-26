namespace Diffract.Leftovers.Cleanup;

public enum StepOutcome
{
    Done,

    /// <summary>Not run: what it expected was no longer true, or an earlier step of its leftover did not run.</summary>
    Skipped,

    /// <summary>Run, and git or Windows refused it.</summary>
    Failed,
}

/// <summary>What happened to one step of a cleanup, or of its undo.</summary>
/// <param name="Leftover">The leftover's place in the plan.</param>
/// <param name="Step">The step's place in that leftover's steps. An undo uses the place of the step it takes back.</param>
/// <param name="Reason">Why it was skipped or failed, in plain English. Null when it was done.</param>
public sealed record StepResult(int Leftover, int Step, StepKind Kind, string Description, StepOutcome Outcome, string? Reason);

/// <summary>What a cleanup, or its undo, did, in the order it did it.</summary>
public sealed record CleanupResult(string CleanupId, IReadOnlyList<StepResult> Steps);
