import { useCallback, useState } from "react";
import { needsAttention, outcomeLine, summarize } from "../cleanup/outcome";
import { useCommands } from "../commands/Commands";
import type { Outcome } from "../commands/commandRun";
import { messageOf } from "../model/format";
import type { CleanupPlanView, CleanupResult, HistoryRef, StashApplyResult, StepRef } from "../protocol";
import type { RpcClient } from "../rpc";
import { stashLabel } from "./stashes";

export interface StashActions {
  /** Applies the stash to `worktree` and keeps it. */
  apply(stash: HistoryRef): void;
  /** Applies it, then opens the drop dialog only when the apply had no conflicts. */
  applyAndDrop(stash: HistoryRef): void;
  /** Plans dropping it, which opens the confirm dialog. */
  drop(stash: HistoryRef): void;
  /** The drop the dialog shows. Null when it is closed. */
  plan: CleanupPlanView | null;
  cancel(): void;
  /** Drops the stash as planned: a backup tag first. The command window then offers "Undo drop". */
  confirm(leaveOut: StepRef[]): void;
}

interface Where {
  client: RpcClient;
  repoId: string;
  /** The worktree Changes shows, which a stash is applied to. */
  worktree: string;
  showNotice(message: string): void;
}

/**
 * The sidebar's stash actions. Apply runs by the stash's commit, never stash@{n}, since the list shifts as agents
 * stash too. Dropping is a cleanup: planned by the sidecar, shown in the confirm dialog, applied as shown, and undone
 * from the command window.
 */
export function useStashActions(where: Where): StashActions {
  const [plan, setPlan] = useState<CleanupPlanView | null>(null);
  const planDrop = usePlanDrop(where, setPlan);
  const apply = useApply(where, planDrop);
  const confirm = useConfirmDrop(where, plan, setPlan);
  return {
    apply: (stash) => void apply(stash, false),
    applyAndDrop: (stash) => void apply(stash, true),
    drop: (stash) => void planDrop(stash.targetId),
    plan,
    cancel: () => setPlan(null),
    confirm,
  };
}

/** Asks the sidecar for the drop's plan, which opens the dialog. A plan that cannot be made is a notice. */
function usePlanDrop({ client, repoId, showNotice }: Where, setPlan: (plan: CleanupPlanView) => void) {
  return useCallback(
    async (commit: string) => {
      try {
        setPlan(await client.call("cleanup.planStash", { repoId, commit }));
      } catch (error) {
        showNotice(`Could not plan dropping the stash: ${messageOf(error)}`);
      }
    },
    [client, repoId, showNotice, setPlan],
  );
}

/** Applies in the command window, then plans the drop when asked to and the apply was clean. */
function useApply({ client, repoId, worktree }: Where, planDrop: (commit: string) => Promise<void>) {
  const commands = useCommands();
  return useCallback(
    async (stash: HistoryRef, thenDrop: boolean) => {
      const result = await commands.run({
        kind: "stash",
        title: `Apply stash "${stashLabel(stash)}"`,
        call: (operation) => client.call("stash.apply", { repoId, worktree, commit: stash.targetId, operation }),
        outcome: (r) => applyOutcome(r, thenDrop),
      });
      if (thenDrop && result?.conflicts === false) await planDrop(stash.targetId);
    },
    [commands, client, repoId, worktree, planDrop],
  );
}

/** Runs the plan the dialog shows, and offers to undo the drop from the command window. */
function useConfirmDrop({ client, repoId }: Where, plan: CleanupPlanView | null, setPlan: (plan: CleanupPlanView | null) => void) {
  const commands = useCommands();
  const undo = useCallback(
    (cleanupId: string) =>
      void commands.run({
        kind: "cleanup",
        title: "Undo the drop",
        call: (operation) => client.call("cleanup.undo", { repoId, cleanupId, operation }),
        outcome: (r) => cleanupOutcome(r, true, []),
      }),
    [commands, client, repoId],
  );
  return useCallback(
    (leaveOut: StepRef[]) => {
      if (!plan) return;
      setPlan(null);
      void commands.run({
        kind: "cleanup",
        title: "Drop the stash",
        call: (operation) => client.call("cleanup.apply", { repoId, planId: plan.planId, leaveOut, operation }),
        outcome: (r) => ({ ...cleanupOutcome(r, false, leaveOut), followUp: dropped(r) ? { label: "Undo drop", run: () => undo(r.cleanupId) } : undefined }),
      });
    },
    [plan, setPlan, commands, client, repoId, undo],
  );
}

/** A conflict is a warning: the stash is kept, and for "Apply and drop" it is not dropped either. */
function applyOutcome(result: StashApplyResult, thenDrop: boolean): Outcome {
  if (!result.conflicts) return { message: result.message, tone: "done" };
  return { message: thenDrop ? `${result.message} It was not dropped.` : result.message, tone: "warning" };
}

function cleanupOutcome(result: CleanupResult, undo: boolean, leaveOut: StepRef[]): Outcome {
  const line = outcomeLine(summarize(result, undo), undo);
  return { message: undo ? `${line} The stash is back, at the top of the list.` : line, tone: needsAttention(result, leaveOut) ? "warning" : "done" };
}

function dropped(result: CleanupResult): boolean {
  return result.steps.some((s) => s.kind === "DropStash" && s.outcome === "Done");
}
