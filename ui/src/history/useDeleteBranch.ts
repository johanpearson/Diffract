import { useCallback, useState } from "react";
import { needsAttention, outcomeLine, summarize } from "../cleanup/outcome";
import { useUndoCleanup } from "../cleanup/useUndoCleanup";
import { useCommands } from "../commands/Commands";
import type { CleanupPlanView, CleanupResult, StepRef } from "../protocol";
import type { RpcClient } from "../rpc";

/** A delete the dialog is asking about: the branch, and the steps that would remove it. */
export interface BranchPlan {
  branch: string;
  plan: CleanupPlanView;
}

export interface DeleteBranch {
  /** Plans deleting the branch, then opens the dialog. A branch that cannot be deleted says why in the command window instead. */
  start(branch: string): void;
  /** The dialog's plan. Null when it is closed. */
  plan: BranchPlan | null;
  cancel(): void;
  /** Deletes the branch, with its backup tag first when the plan has one. The command window then offers "Undo delete". */
  confirm(leaveOut: StepRef[]): void;
}

/**
 * "Delete branch" in the history menu: plan, then apply, like any cleanup. Nothing is removed until the dialog is
 * confirmed, and a branch checked out in a worktree is never offered a dialog.
 */
export function useDeleteBranch(client: RpcClient, repoId: string): DeleteBranch {
  const commands = useCommands();
  const [plan, setPlan] = useState<BranchPlan | null>(null);
  const undo = useUndoCleanup(client, repoId, "Undo the delete");

  const start = useCallback(
    async (branch: string) => {
      const planned = await commands.run({
        kind: "cleanup",
        title: `Plan deleting ${branch}`,
        call: () => client.call("cleanup.planBranch", { repoId, branch }),
        outcome: (p) => refusal(p) ?? { message: `Planned deleting ${branch}.`, tone: "done" },
      });
      if (planned && refusal(planned) === null) setPlan({ branch, plan: planned });
    },
    [commands, client, repoId],
  );

  const confirm = useCallback(
    (leaveOut: StepRef[]) => {
      if (!plan) return;
      setPlan(null);
      void commands.run({
        kind: "cleanup",
        title: `Delete branch ${plan.branch}`,
        call: (operation) => client.call("cleanup.apply", { repoId, planId: plan.plan.planId, leaveOut, operation }),
        outcome: (r) => ({
          message: outcomeLine(summarize(r, false), false),
          tone: needsAttention(r, leaveOut) ? "warning" : "done",
          followUp: deleted(r) ? { label: "Undo delete", run: () => undo(r.cleanupId) } : undefined,
        }),
      });
    },
    [plan, commands, client, repoId, undo],
  );

  return { start: (branch) => void start(branch), plan, cancel: () => setPlan(null), confirm };
}

/** Why the branch cannot be deleted, said in the window since no dialog follows. Null when it can. */
function refusal({ leftovers }: CleanupPlanView): { message: string; tone: "warning" } | null {
  const why = leftovers[0]?.refused;
  return why == null ? null : { message: why, tone: "warning" };
}

function deleted(result: CleanupResult): boolean {
  return result.steps.some((s) => s.kind === "DeleteBranch" && s.outcome === "Done");
}
