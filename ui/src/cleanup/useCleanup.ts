import { useCallback, useEffect, useMemo, useState } from "react";
import { useCommands } from "../commands/Commands";
import { messageOf } from "../model/format";
import type { Row } from "../model/rows";
import type { CleanupPlanView, CleanupResult, PastCleanup, StepRef } from "../protocol";
import type { RpcClient } from "../rpc";
import { keepsOnly, needsAttention, outcomeLine, plannedCount, summarize } from "./outcome";
import { selectedRows, toSelected, withMergedBranches } from "./selection";

/** The last cleanup or undo, as the result shows it. */
export interface Shown {
  result: CleanupResult;
  undo: boolean;
}

export interface Cleanup {
  /** The rows ticked now. A ticked row that went away or into use is not among them. */
  selected: Row[];
  isSelected(key: string): boolean;
  toggle(key: string): void;
  selectMergedBranches(): void;
  /** Plans the selection and opens the confirm dialog. */
  start(): void;
  /** "Keep these" on one row of orphaned commits: plans that row alone, which puts them under one backup tag. */
  keep(row: Row): void;
  planning: boolean;
  /** The plan the dialog shows. Null when it is closed. */
  plan: CleanupPlanView | null;
  cancel(): void;
  /** Applies the plan the dialog shows, and nothing else, without the steps left out. */
  confirm(leaveOut: StepRef[]): void;
  shown: Shown | null;
  dismiss(): void;
  /** Past cleanups, newest first. */
  history: PastCleanup[];
  undo(cleanupId: string): void;
  /** A cleanup or undo is running. */
  busy: boolean;
}

/**
 * Selecting leftovers, the confirm dialog, and applying and undoing in the command window. The sidecar answers an
 * apply or an undo only after it has sent the repo as it is afterwards, so the rows have already changed by then.
 */
export function useCleanup(client: RpcClient, repoId: string, rows: Row[], showNotice: (message: string) => void): Cleanup {
  const commands = useCommands();
  const selection = useSelection(rows);
  const { selected, clear } = selection;
  const { plan, setPlan, planning, planFor } = usePlanning(client, repoId, showNotice);
  const [shown, setShown] = useState<Shown | null>(null);
  const [history, loadHistory] = usePastCleanups(client, repoId, showNotice);
  const [busy, setBusy] = useState(false);

  /** Runs an apply or an undo in the command window, then shows what it did and reads the history again. */
  const run = useCallback(
    async (title: string, undo: boolean, leftOut: StepRef[], call: (operation: string) => Promise<CleanupResult>) => {
      setBusy(true);
      try {
        const result = await commands.run({
          kind: "cleanup",
          title,
          call,
          outcome: (r) => ({ message: outcomeLine(summarize(r, undo), undo), tone: needsAttention(r, leftOut) ? "warning" : "done" }),
        });
        if (result) setShown({ result, undo });
        if (result && !undo) clear();
      } finally {
        setBusy(false);
        loadHistory();
      }
    },
    [commands, loadHistory, clear],
  );

  const confirm = useCallback(
    (leaveOut: StepRef[]) => {
      if (!plan) return;
      setPlan(null);
      const title = keepsOnly(plan) ? "Keep these commits" : `Clean up ${count(plannedCount(plan))}`;
      void run(title, false, leaveOut, (operation) => client.call("cleanup.apply", { repoId, planId: plan.planId, leaveOut, operation }));
    },
    [client, plan, repoId, run],
  );

  const undo = useCallback(
    (cleanupId: string) => void run("Undo the cleanup", true, [], (operation) => client.call("cleanup.undo", { repoId, cleanupId, operation })),
    [client, repoId, run],
  );

  return {
    selected,
    isSelected: selection.isSelected,
    toggle: selection.toggle,
    selectMergedBranches: selection.selectMergedBranches,
    start: () => void planFor(selected),
    keep: (row) => void planFor([row]),
    planning,
    plan,
    cancel: () => setPlan(null),
    confirm,
    shown,
    dismiss: () => setShown(null),
    history,
    undo,
    busy,
  };
}

/** The ticked rows. A row that went away or into use drops out of `selected` by itself. */
function useSelection(rows: Row[]) {
  const [ticked, setTicked] = useState<ReadonlySet<string>>(new Set());
  const selected = useMemo(() => selectedRows(rows, ticked), [rows, ticked]);
  const clear = useCallback(() => setTicked(new Set()), []);
  return {
    selected,
    clear,
    isSelected: (key: string) => ticked.has(key),
    toggle: (key: string) => setTicked((before) => toggled(before, key)),
    selectMergedBranches: () => setTicked((before) => withMergedBranches(rows, before)),
  };
}

/** Asks the sidecar for a plan, which opens the confirm dialog. A plan that cannot be made is a notice. */
function usePlanning(client: RpcClient, repoId: string, showNotice: (message: string) => void) {
  const [planning, setPlanning] = useState(false);
  const [plan, setPlan] = useState<CleanupPlanView | null>(null);
  const planFor = useCallback(
    async (chosen: Row[]) => {
      setPlanning(true);
      try {
        setPlan(await client.call("cleanup.plan", { repoId, leftovers: chosen.map((row) => toSelected(row.leftover)) }));
      } catch (error) {
        showNotice(`Could not plan the cleanup: ${messageOf(error)}`);
      } finally {
        setPlanning(false);
      }
    },
    [client, repoId, showNotice],
  );
  return { plan, setPlan, planning, planFor };
}

/** The operation log, read when the view opens and again after each cleanup or undo. */
function usePastCleanups(client: RpcClient, repoId: string, showNotice: (message: string) => void): [PastCleanup[], () => void] {
  const [history, setHistory] = useState<PastCleanup[]>([]);
  const load = useCallback(() => {
    client.call("cleanup.history", { repoId }).then(setHistory, (error: unknown) => showNotice(`Could not read past cleanups: ${messageOf(error)}`));
  }, [client, repoId, showNotice]);
  useEffect(load, [load]);
  return [history, load];
}

function toggled(set: ReadonlySet<string>, key: string): Set<string> {
  const next = new Set(set);
  if (!next.delete(key)) next.add(key);
  return next;
}

function count(n: number): string {
  return n === 1 ? "1 leftover" : `${n} leftovers`;
}
