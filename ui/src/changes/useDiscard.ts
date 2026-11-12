import { useCallback, useState } from "react";
import { needsAttention, outcomeLine, summarize } from "../cleanup/outcome";
import { useCommands } from "../commands/Commands";
import type { ChangesStageParams, CleanupPlanView, CleanupResult, Hunk, StepRef } from "../protocol";
import type { RpcClient } from "../rpc";
import { pathsOf, type Entry } from "./entries";

export interface Discard {
  /** The dialog's plan. Null when it is closed. */
  plan: CleanupPlanView | null;
  /** Plans discarding the unstaged changes of these rows, then opens the dialog. Nothing is thrown away yet. */
  files(entries: readonly Entry[]): void;
  /** Plans discarding one hunk of an unstaged row. */
  hunk(entry: Entry, hunk: Hunk): void;
  cancel(): void;
  /** Throws away what the dialog listed. The command window then offers "Undo discard". */
  confirm(leaveOut: StepRef[]): void;
}

interface Where {
  client: RpcClient;
  repoId: string;
  worktree: string;
  /** Reads the changes again, as a discard or its undo changes files without any other answer. */
  reload(): void;
  /** Shows why planning failed, such as a hunk that changed since it was shown. */
  fail(message: string): void;
}

/**
 * Discard: plan, then apply, like any cleanup. Planning only reads; nothing is thrown away until the dialog is
 * confirmed. The sidecar copies each file first, so the command window's "Undo discard" can write it back.
 */
export function useDiscard({ client, repoId, worktree, reload, fail }: Where): Discard {
  const commands = useCommands();
  const [plan, setPlan] = useState<CleanupPlanView | null>(null);

  const ask = useCallback(
    (params: Omit<ChangesStageParams, "repoId" | "worktree">) =>
      client
        .call("cleanup.planDiscard", { repoId, worktree, ...params } as ChangesStageParams)
        .then(setPlan)
        .catch((error: unknown) => fail(error instanceof Error ? error.message : String(error))),
    [client, repoId, worktree, fail],
  );

  const undo = useUndo(client, repoId, reload);
  const confirm = useCallback(
    (leaveOut: StepRef[]) => {
      if (!plan) return;
      setPlan(null);
      void commands
        .run({
          kind: "cleanup",
          title: title(plan),
          call: (operation) => client.call("cleanup.apply", { repoId, planId: plan.planId, leaveOut, operation }),
          outcome: (r) => ({
            message: outcomeLine(summarize(r, false), false),
            tone: needsAttention(r, leaveOut) ? "warning" : "done",
            followUp: discarded(r) ? { label: "Undo discard", run: () => undo(r.cleanupId) } : undefined,
          }),
        })
        .finally(reload);
    },
    [plan, commands, client, repoId, undo, reload],
  );

  return {
    plan,
    files: (entries) => void ask({ paths: pathsOf(entries) }),
    hunk: (entry, hunk) => void ask({ hunk: { path: entry.file.path, oldPath: null, id: hunk.id } }),
    cancel: () => setPlan(null),
    confirm,
  };
}

function useUndo(client: RpcClient, repoId: string, reload: () => void) {
  const commands = useCommands();
  return useCallback(
    (cleanupId: string) =>
      void commands
        .run({
          kind: "cleanup",
          title: "Undo the discard",
          call: (operation) => client.call("cleanup.undo", { repoId, cleanupId, operation }),
          outcome: (r) => ({ message: outcomeLine(summarize(r, true), true), tone: needsAttention(r, []) ? "warning" : "done" }),
        })
        .finally(reload),
    [commands, client, repoId, reload],
  );
}

function title({ leftovers }: CleanupPlanView): string {
  return leftovers.length === 1 ? `Discard ${leftovers[0]!.name}` : `Discard ${leftovers.length} files`;
}

function discarded(result: CleanupResult): boolean {
  return result.steps.some((s) => s.kind === "DiscardChanges" && s.outcome === "Done");
}
