import { CleanupDialog } from "../cleanup/CleanupDialog";
import type { StepRef } from "../protocol";
import type { BranchPlan } from "./useDeleteBranch";

interface Props {
  delete: BranchPlan;
  onConfirm(leaveOut: StepRef[]): void;
  onCancel(): void;
}

/** The confirm dialog for deleting a branch, in the cleanup dialog's words, saying whether a backup tag comes first. */
export function DeleteBranchDialog({ delete: { branch, plan }, onConfirm, onCancel }: Props) {
  const backedUp = plan.leftovers.some((l) => l.steps.some((s) => s.kind === "Backup"));
  const wording = {
    title: `Delete branch ${branch}?`,
    confirm: "Delete",
    note: backedUp
      ? "This branch has commits that exist nowhere else, so a backup tag keeps them first. Undo brings the branch back from the command window, or from past cleanups."
      : "Everything on this branch is also reachable from elsewhere, so no backup tag is made. Undo brings the branch back from the command window, or from past cleanups, while git still has the commits.",
  };
  return <CleanupDialog plan={plan} onConfirm={onConfirm} onCancel={onCancel} wording={wording} />;
}
