import type { CleanupPlanView, CleanupResult, StepRef, StepResult } from "../protocol";

/** A step's place as one string, for sets of unticked steps. */
export function stepKey(ref: StepRef): string {
  return `${ref.leftover}/${ref.step}`;
}

/** The optional steps the user unticked, as `cleanup.apply` takes them. */
export function leaveOutOf(plan: CleanupPlanView, unticked: ReadonlySet<string>): StepRef[] {
  return plan.leftovers.flatMap((leftover, l) =>
    leftover.steps.flatMap((step, s) => (step.optional && unticked.has(stepKey({ leftover: l, step: s })) ? [{ leftover: l, step: s }] : [])),
  );
}

/** Leftovers that have steps to run. Refused ones are only listed, with why. */
export function plannedCount(plan: CleanupPlanView): number {
  return plan.leftovers.filter((l) => l.refused === null).length;
}

/** Every planned leftover is orphaned commits, which "Keep these" only keeps: nothing is removed. */
export function keepsOnly(plan: CleanupPlanView): boolean {
  const planned = plan.leftovers.filter((l) => l.refused === null);
  return planned.length > 0 && planned.every((l) => l.kind === "OrphanedCommit");
}

const keeps = (s: StepResult) => s.kind === "Backup" || s.kind === "KeepCommits";

/** What a cleanup or undo did, grouped as the result shows it. */
export interface CleanupSummary {
  /** Done, apart from backups: removed by a cleanup, put back by an undo. */
  done: StepResult[];
  /** Backup tags made, which hold work that exists nowhere else. Only a cleanup makes them. */
  backups: StepResult[];
  /** Skipped or failed, each with why. Steps the user left out are here too, saying so. */
  notDone: StepResult[];
}

export function summarize(result: CleanupResult, undo: boolean): CleanupSummary {
  const done = result.steps.filter((s) => s.outcome === "Done");
  return {
    done: done.filter((s) => undo || !keeps(s)),
    backups: undo ? [] : done.filter(keeps),
    notDone: result.steps.filter((s) => s.outcome !== "Done"),
  };
}

/** Something to read in the result: a step failed, or was skipped other than by the user's choice. */
export function needsAttention(result: CleanupResult, leftOut: StepRef[]): boolean {
  const chosen = new Set(leftOut.map(stepKey));
  return result.steps.some((s) => s.outcome === "Failed" || (s.outcome === "Skipped" && !chosen.has(stepKey(s))));
}

/** One line for the command window, such as "2 steps done. Kept 1 backup tag. 1 step not done: see the result." */
export function outcomeLine(summary: CleanupSummary, undo: boolean): string {
  const parts = [
    `${count(summary.done.length, "step")} ${undo ? "undone" : "done"}.`,
    ...(summary.backups.length > 0 ? [`Kept ${count(summary.backups.length, "backup tag")}.`] : []),
    ...(summary.notDone.length > 0 ? [`${count(summary.notDone.length, "step")} not done: see the result.`] : []),
  ];
  return parts.join(" ");
}

function count(n: number, noun: string): string {
  return n === 1 ? `1 ${noun}` : `${n} ${noun}s`;
}
