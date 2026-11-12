import { CleanupDialog } from "../cleanup/CleanupDialog";
import type { StepRef } from "../protocol";
import type { RemoteBranchPlan } from "./useDeleteRemoteBranch";

interface Props {
  delete: RemoteBranchPlan;
  onConfirm(leaveOut: StepRef[]): void;
  onCancel(): void;
}

/**
 * The confirm dialog for deleting a branch on a remote. It says in plain words that this changes the remote, for
 * everyone who uses it, and the button stays off until the user ticks that they understand.
 */
export function DeleteRemoteBranchDialog({ delete: { remote, branch, plan }, onConfirm, onCancel }: Props) {
  const backedUp = plan.leftovers.some((l) => l.steps.some((s) => s.kind === "Backup"));
  const wording = {
    title: `Delete ${branch} on ${remote}?`,
    confirm: `Delete on ${remote}`,
    note: `This changes ${remote}, not only this computer. The branch ${branch} is removed there for everyone who uses ${remote}. ${
      backedUp
        ? "It has commits that exist nowhere else, so a backup tag keeps them here first."
        : "Its commits are also reachable from elsewhere, so no backup tag is made."
    } Undo pushes it back to the commit it had, from the command window or past cleanups, if ${remote} accepts it and git still has the commit.`,
    acknowledge: `I understand this removes it on ${remote}`,
  };
  return <CleanupDialog plan={plan} onConfirm={onConfirm} onCancel={onCancel} wording={wording} />;
}
