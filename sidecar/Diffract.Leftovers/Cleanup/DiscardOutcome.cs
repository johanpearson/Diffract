namespace Diffract.Leftovers.Cleanup;

/// <summary>What a discard or its undo did: the outcome, why it did not run or failed, and what takes a discard back.</summary>
internal sealed record DiscardOutcome(StepOutcome Outcome, string? Reason)
{
    /// <summary>For a discard that was done: the copy of the file and the file as the discard left it.</summary>
    public FileUndo? Undo { get; init; }

    public static DiscardOutcome Done() => new(StepOutcome.Done, null);

    public static DiscardOutcome Skipped(string reason) => new(StepOutcome.Skipped, reason);

    public static DiscardOutcome Failed(string reason) => new(StepOutcome.Failed, reason);
}
