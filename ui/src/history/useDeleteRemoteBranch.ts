import { useCallback, useState } from "react";
import { needsAttention, outcomeLine, summarize } from "../cleanup/outcome";
import { useUndoCleanup } from "../cleanup/useUndoCleanup";
import { useCommands } from "../commands/Commands";
import type { CleanupPlanView, CleanupResult, StepRef } from "../protocol";
import type { RpcClient } from "../rpc";

/** A delete on a remote the dialog is asking about: where, which branch, and the steps that would do it. */
export interface RemoteBranchPlan {
  remote: string;
  branch: string;
  plan: CleanupPlanView;
}

export interface DeleteRemoteBranch {
  /**
   * Asks the remote what it has, in the command window, then plans deleting the branch there and opens the dialog. A
   * branch that cannot be deleted, such as the remote's default, says why in the window instead.
   */
  start(remote: string, branch: string): void;
  /** The dialog's plan. Null when it is closed. */
  plan: RemoteBranchPlan | null;
  cancel(): void;
  /** Deletes the branch on the remote, with its backup tag first when the plan has one. The window then offers "Undo delete". */
  confirm(leaveOut: StepRef[]): void;
}

/**
 * "Delete remote branch": plan, then apply, like any cleanup, but the only one that changes something other than this
 * computer. Planning asks the remote, which can take a while, so it runs in the command window, which can cancel it.
 * Nothing is deleted until the dialog is confirmed with its extra tick.
 */
export function useDeleteRemoteBranch(client: RpcClient, repoId: string): DeleteRemoteBranch {
  const commands = useCommands();
  const [plan, setPlan] = useState<RemoteBranchPlan | null>(null);
  const undo = useUndoCleanup(client, repoId, "Undo the delete");

  const start = useCallback(
    async (remote: string, branch: string) => {
      const planned = await commands.run({
        kind: "cleanup",
        title: `Ask ${remote} about ${branch}`,
        cancellable: true,
        call: (_operation, signal) => client.call("cleanup.planRemoteBranch", { repoId, remote, branch }, { signal }),
        outcome: (p) => refusal(p) ?? { message: `Planned deleting ${branch} on ${remote}.`, tone: "done" },
      });
      if (planned && refusal(planned) === null) setPlan({ remote, branch, plan: planned });
    },
    [commands, client, repoId],
  );

  const confirm = useCallback(
    (leaveOut: StepRef[]) => {
      if (!plan) return;
      setPlan(null);
      void commands.run({
        kind: "cleanup",
        title: `Delete ${plan.branch} on ${plan.remote}`,
        call: (operation) => client.call("cleanup.apply", { repoId, planId: plan.plan.planId, leaveOut, operation }),
        outcome: (r) => ({
          message: withReasons(r),
          tone: needsAttention(r, leaveOut) ? "warning" : "done",
          followUp: deleted(r) ? { label: "Undo delete", run: () => undo(r.cleanupId) } : undefined,
        }),
      });
    },
    [plan, commands, client, repoId, undo],
  );

  return { start: (remote, branch) => void start(remote, branch), plan, cancel: () => setPlan(null), confirm };
}

/** Why the branch cannot be deleted, said in the window since no dialog follows. Null when it can. */
function refusal({ leftovers }: CleanupPlanView): { message: string; tone: "warning" } | null {
  const why = leftovers[0]?.refused;
  return why == null ? null : { message: why, tone: "warning" };
}

/** The usual one-liner, then why each step was not done, in git's own words, since nothing else may be on screen to read them. */
function withReasons(result: CleanupResult): string {
  const summary = summarize(result, false);
  const reasons = summary.notDone.flatMap((s) => (s.reason ? [s.reason] : []));
  return [outcomeLine(summary, false), ...reasons].join(" ");
}

function deleted(result: CleanupResult): boolean {
  return result.steps.some((s) => s.kind === "DeleteOnRemote" && s.outcome === "Done");
}
