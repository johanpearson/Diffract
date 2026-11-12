using Diffract.Git.Changes;

namespace Diffract.Leftovers.Cleanup;

/// <summary>One file, or one hunk of it, the user picked to discard.</summary>
/// <param name="Hunk">What the dialog calls the hunk, such as "the change at line 12". Null for a whole file.</param>
public sealed record DiscardTarget(string Worktree, string Path, DiscardMode Mode, byte[]? Patch = null, string? Hunk = null);

/// <summary>
/// Plan, then apply, for the Changes view's Discard: one leftover per file, with one step that throws the changes away.
/// The step expects the file exactly as it is now, so applying it can only throw away what the dialog described.
/// It reads each file once, to know its bytes; it runs no git.
/// </summary>
public static class DiscardPlanner
{
    private const string UndoNote = "Undo puts the file back as it is now, for as long as Diffract stays open.";

    public static CleanupPlan Plan(IReadOnlyList<DiscardTarget> targets) => new([.. targets.Select(PlanOne)]);

    private static LeftoverPlan PlanOne(DiscardTarget target)
    {
        var full = WorktreeFile.Resolve(target.Worktree, target.Path);
        var leftover = new Leftover(LeftoverKind.DiscardedChange, target.Path, full, null, Verdict.NeedsReview, null, Describe(target));
        var hash = FileState.HashOf(full);
        if (hash is null && target.Mode != DiscardMode.RestoreFromIndex)
            return LeftoverPlan.Refuse(leftover, "The file is no longer there. Refresh to see the changes as they are now.");
        var step = new CleanupStep
        {
            Kind = StepKind.DiscardChanges,
            Description = Describe(target),
            Discard = new DiscardWork(target.Worktree, target.Path, target.Mode, target.Patch),
            Expects = [new Expectation.FileIs(target.Worktree, target.Path, hash)],
            UndoNote = UndoNote,
        };
        return new LeftoverPlan(leftover, [step]);
    }

    private static string Describe(DiscardTarget target) => target.Mode switch
    {
        DiscardMode.DeleteNewFile => $"Delete the new file {target.Path}",
        DiscardMode.ReverseHunk => $"Discard {target.Hunk ?? "one change"} in {target.Path}",
        _ => $"Discard all unstaged changes in {target.Path}",
    };
}
