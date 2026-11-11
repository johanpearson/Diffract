import { CleanupDialog } from "../cleanup/CleanupDialog";
import type { StepRef } from "../protocol";
import type { PrunePlan } from "./usePruneRemote";

interface Props {
  prune: PrunePlan;
  onConfirm(leaveOut: StepRef[]): void;
  onCancel(): void;
}

/** The confirm dialog for pruning, in the cleanup dialog's words: only these copies go, never the remote's branches. */
export function PruneDialog({ prune: { remote, plan }, onConfirm, onCancel }: Props) {
  const count = plan.leftovers.length;
  const wording = {
    title: `Remove ${count === 1 ? "1 branch" : `${count} branches`} that ${remote} no longer has?`,
    confirm: "Remove",
    note: `This removes this computer's copies only. Nothing on ${remote} is touched, and no backup tags are made. Undo works from the command window, or from past cleanups, while git still has the commits.`,
  };
  return <CleanupDialog plan={plan} onConfirm={onConfirm} onCancel={onCancel} wording={wording} />;
}
