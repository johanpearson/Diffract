using Diffract.Leftovers;
using Diffract.Leftovers.Cleanup;

namespace Diffract.Sidecar;

// Params and results of the cleanup methods, as in Protocol.cs. They mirror ui/src/protocol.ts.

/// <summary>A leftover as the repo state lists it: its kind, name and path pick it out.</summary>
internal sealed record SelectedLeftover(LeftoverKind? Kind, string? Name, string? Path);

internal sealed record CleanupPlanParams(string? RepoId, IReadOnlyList<SelectedLeftover>? Leftovers);

/// <param name="LeaveOut">Steps the user unticked. Only steps with <c>optional</c> set can be left out.</param>
internal sealed record CleanupApplyParams(string? RepoId, string? PlanId, IReadOnlyList<StepRef>? LeaveOut);

internal sealed record CleanupHistoryParams(string? RepoId);

/// <summary>The params of <c>cleanup.inspect</c>: one leftover, as the repo state lists it.</summary>
internal sealed record CleanupInspectParams(string? RepoId, SelectedLeftover? Leftover);

internal sealed record CleanupUndoParams(string? RepoId, string? CleanupId);

/// <summary>The result of <c>cleanup.plan</c>: what the confirm dialog shows, and the id to apply it by.</summary>
internal sealed record CleanupPlanView(string PlanId, IReadOnlyList<LeftoverPlanView> Leftovers);

/// <param name="Refused">Why nothing is planned for it, such as "In use". Null when it has steps.</param>
internal sealed record LeftoverPlanView(LeftoverKind Kind, string Name, string? Path, string? Refused, IReadOnlyList<StepView> Steps);

/// <summary>One step, in plain English.</summary>
/// <param name="Undoable">Git can take it back. When false, the dialog says it cannot be undone, with <see cref="UndoNote"/> when there is one.</param>
/// <param name="UndoNote">A limit on undo, or what to do instead, such as "Restore it from the Recycle Bin."</param>
/// <param name="Optional">Has its own checkbox, ticked by default. Its place can be sent in <c>leaveOut</c>.</param>
internal sealed record StepView(StepKind Kind, string Description, bool Undoable, string? UndoNote, bool Optional)
{
    public static StepView Of(CleanupStep step) => new(step.Kind, step.Description, step.Undo is not null, step.UndoNote, step.Optional);
}
