import { CleanupDialog } from "../cleanup/CleanupDialog";
import type { CleanupPlanView, StepRef } from "../protocol";

interface Props {
  plan: CleanupPlanView;
  onConfirm(leaveOut: StepRef[]): void;
  onCancel(): void;
}

/** The confirm dialog for discarding, in the cleanup dialog's words: what goes, what stays, and how Undo works. */
export function DiscardDialog({ plan, onConfirm, onCancel }: Props) {
  const count = plan.leftovers.length;
  const wording = {
    title: count === 1 ? "Discard these changes?" : `Discard changes in ${count} files?`,
    confirm: "Discard",
    note: "This throws away changes that are not staged. Staged changes stay. Diffract keeps a copy of each file until it closes, so Undo can write it back, but only while the file is still as the discard left it.",
  };
  return <CleanupDialog plan={plan} onConfirm={onConfirm} onCancel={onCancel} wording={wording} />;
}
