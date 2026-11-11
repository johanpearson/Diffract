import { useCallback, useState } from "react";
import { needsAttention, outcomeLine, summarize } from "../cleanup/outcome";
import { useCommands } from "../commands/Commands";
import type { CleanupPlanView, CleanupResult, RepoState, StepRef } from "../protocol";
import type { RpcClient } from "../rpc";

/** A prune the dialog is asking about: the remote, and what removing its vanished branches would do. */
export interface PrunePlan {
  remote: string;
  plan: CleanupPlanView;
}

export interface PruneRemote {
  /** Asks the remote which branches it has, in the command window, then opens the dialog. */
  start(remote: string): void;
  /** The dialog's plan. Null when it is closed. */
  plan: PrunePlan | null;
  cancel(): void;
  /** Removes the listed remote-tracking branches, here only. The command window then offers "Undo prune". */
  confirm(leaveOut: StepRef[]): void;
}

/**
 * "Prune remote branches": plan, then apply, like any cleanup. Planning asks the remote with a read that can take a
 * while on a slow network, so it runs in the command window, which can cancel it. Nothing is removed until the
 * dialog is confirmed, and a remote that cannot be reached removes nothing.
 */
export function usePruneRemote(client: RpcClient, repo: RepoState | null): PruneRemote {
  const repoId = repo?.repoId ?? "";
  const commands = useCommands();
  const [plan, setPlan] = useState<PrunePlan | null>(null);

  const start = useCallback(
    async (remote: string) => {
      const planned = await commands.run({
        kind: "cleanup",
        title: `Ask ${remote} which branches it has`,
        cancellable: true,
        call: (_operation, signal) => client.call("cleanup.planPrune", { repoId, remote }, { signal }),
        outcome: (p) => ({ message: found(remote, p), tone: "done" }),
      });
      if (planned && planned.leftovers.length > 0) setPlan({ remote, plan: planned });
    },
    [commands, client, repoId],
  );

  const undo = useUndo(client, repoId);
  const confirm = useCallback(
    (leaveOut: StepRef[]) => {
      if (!plan) return;
      setPlan(null);
      void commands.run({
        kind: "cleanup",
        title: `Prune ${plan.remote}`,
        call: (operation) => client.call("cleanup.apply", { repoId, planId: plan.plan.planId, leaveOut, operation }),
        outcome: (r) => ({
          message: outcomeLine(summarize(r, false), false),
          tone: needsAttention(r, leaveOut) ? "warning" : "done",
          followUp: removed(r) ? { label: "Undo prune", run: () => undo(r.cleanupId) } : undefined,
        }),
      });
    },
    [plan, commands, client, repoId, undo],
  );

  return { start: (remote) => void start(remote), plan, cancel: () => setPlan(null), confirm };
}

function useUndo(client: RpcClient, repoId: string) {
  const commands = useCommands();
  return useCallback(
    (cleanupId: string) =>
      void commands.run({
        kind: "cleanup",
        title: "Undo the prune",
        call: (operation) => client.call("cleanup.undo", { repoId, cleanupId, operation }),
        outcome: (r) => ({ message: outcomeLine(summarize(r, true), true), tone: needsAttention(r, []) ? "warning" : "done" }),
      }),
    [commands, client, repoId],
  );
}

/** What the check found, said in the window when nothing needs removing, since no dialog follows. */
function found(remote: string, { leftovers }: CleanupPlanView): string {
  if (leftovers.length === 0) return `Nothing to prune: ${remote} still has every branch you have a copy of.`;
  return `${remote} no longer has ${leftovers.length === 1 ? "1 branch" : `${leftovers.length} branches`}.`;
}

function removed(result: CleanupResult): boolean {
  return result.steps.some((s) => s.kind === "DeleteRemoteBranch" && s.outcome === "Done");
}
