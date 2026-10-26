import type { CleanupPlanView, CleanupResult, Leftover, LeftoverPlanView, PastCleanup, RepoChange, RepoState, SelectedLeftover, StepRef, StepResult, StepView } from "../protocol";

/** What the fake's cleanup methods read and change on the fake sidecar. */
export interface CleanupHost {
  state(repoId: string): RepoState;
  change(repoId: string, update: (state: RepoState) => RepoState, changes?: RepoChange[]): void;
}

const EarlierStep = "Skipped, as an earlier step for the same leftover did not run.";

/** A plan the fake keeps, as the sidecar does, until it is applied. */
interface Kept {
  repoId: string;
  plan: CleanupPlanView;
  leftovers: (Leftover | null)[];
}

/**
 * cleanup.* as the sidecar answers them, against the fake's leftovers. Applying removes the leftovers it cleaned up
 * and sends repo.changed before answering; undoing puts them back. Steps are made up per kind, worded as the planner
 * words them.
 */
export class FakeCleanups {
  /** Leftovers by name whose first step is skipped on the next apply, with why, as when an agent moved a branch. */
  readonly skips = new Map<string, string>();
  /** Past cleanups, newest first. */
  readonly history: PastCleanup[] = [];
  /** Every cleanup.* asked for, in order. */
  readonly asked: { method: string; params: unknown }[] = [];

  private readonly plans = new Map<string, Kept>();
  private readonly removed = new Map<string, { repoId: string; leftovers: Leftover[] }>();
  private next = 0;

  answer(host: CleanupHost, method: string, params: unknown): unknown {
    this.asked.push({ method, params });
    const p = params as { repoId: string; leftovers?: SelectedLeftover[]; planId?: string; leaveOut?: StepRef[] | null; cleanupId?: string };
    switch (method) {
      case "cleanup.plan":
        return this.plan(host.state(p.repoId), p.leftovers ?? []);
      case "cleanup.apply":
        return this.apply(host, p.repoId, p.planId ?? "", p.leaveOut ?? []);
      case "cleanup.history":
        return this.history;
      case "cleanup.undo":
        return this.undo(host, p.repoId, p.cleanupId ?? "");
    }
    return undefined;
  }

  private plan(state: RepoState, selected: SelectedLeftover[]): CleanupPlanView {
    const found = selected.map((s) => state.leftovers.find((l) => l.kind === s.kind && l.name === s.name && l.path === s.path) ?? null);
    const plan: CleanupPlanView = { planId: `plan-${++this.next}`, leftovers: selected.map((s, i) => planOf(s, found[i] ?? null)) };
    this.plans.set(plan.planId, { repoId: state.repoId, plan, leftovers: found });
    return plan;
  }

  private apply(host: CleanupHost, repoId: string, planId: string, leaveOut: StepRef[]): CleanupResult {
    const kept = this.plans.get(planId);
    if (!kept || kept.repoId !== repoId) throw refused("This plan is no longer there, or was applied already. Select the leftovers again.");
    this.plans.delete(planId);
    const left = new Set(leaveOut.map((r) => `${r.leftover}/${r.step}`));
    const steps = kept.plan.leftovers.flatMap((l, i) => (l.refused === null ? this.run(l, i, left) : []));
    const cleaned = this.cleaned(kept);

    const cleanupId = `cleanup-${++this.next}`;
    this.removed.set(cleanupId, { repoId, leftovers: cleaned });
    host.change(repoId, (s) => withLeftovers(s, s.leftovers.filter((l) => !cleaned.some((c) => same(c, l)))), [{ kind: "Everything", path: null }]);
    const canUndo = steps.some((s) => s.outcome === "Done" && s.kind !== "RecycleFolder");
    this.history.unshift({ cleanupId, at: host.state(repoId).takenAt, undoes: null, leftovers: kept.plan.leftovers.map(named), steps, canUndo });
    return { cleanupId, steps };
  }

  /** The planned leftovers that were not skipped. The skips are used up. */
  private cleaned(kept: Kept): Leftover[] {
    const cleaned = kept.leftovers.filter((l, i): l is Leftover => l !== null && kept.plan.leftovers[i]!.refused === null && !this.skips.has(l.name));
    kept.leftovers.forEach((l) => l && this.skips.delete(l.name));
    return cleaned;
  }

  /** Every step done, apart from those left out, or all skipped when the leftover is in `skips`. */
  private run(leftover: LeftoverPlanView, index: number, leftOut: ReadonlySet<string>): StepResult[] {
    const skip = this.skips.get(leftover.name);
    return leftover.steps.map((step, s) => {
      const result = { leftover: index, step: s, kind: step.kind, description: step.description };
      if (skip !== undefined) return { ...result, outcome: "Skipped" as const, reason: s === 0 ? skip : EarlierStep };
      if (leftOut.has(`${index}/${s}`)) return { ...result, outcome: "Skipped" as const, reason: "Left out, as chosen." };
      return { ...result, outcome: "Done" as const, reason: null };
    });
  }

  private undo(host: CleanupHost, repoId: string, cleanupId: string): CleanupResult {
    const entry = this.history.find((c) => c.cleanupId === cleanupId);
    if (!entry?.canUndo) throw refused("Everything in this cleanup that can be undone is undone already.");
    const back = this.removed.get(cleanupId)?.leftovers ?? [];
    host.change(repoId, (s) => withLeftovers(s, [...s.leftovers, ...back]), [{ kind: "Everything", path: null }]);
    const steps = entry.steps
      .filter((s) => s.outcome === "Done" && s.kind !== "RecycleFolder")
      .reverse()
      .map((s) => ({ ...s, description: `Undo: ${s.description}` }));
    entry.canUndo = false;
    const undoId = `cleanup-${++this.next}`;
    this.history.unshift({ cleanupId: undoId, at: host.state(repoId).takenAt, undoes: cleanupId, leftovers: entry.leftovers, steps, canUndo: false });
    return { cleanupId: undoId, steps };
  }
}

function refused(message: string) {
  return { code: -32011, message, data: { kind: "CleanupRefused" } };
}

function same(a: Leftover, b: Leftover): boolean {
  return a.kind === b.kind && a.name === b.name && a.path === b.path;
}

function named(l: LeftoverPlanView): SelectedLeftover {
  return { kind: l.kind, name: l.name, path: l.path };
}

/** The state with these leftovers, and the counts to match. */
function withLeftovers(state: RepoState, leftovers: Leftover[]): RepoState {
  const of = (verdict: Leftover["verdict"]) => leftovers.filter((l) => l.verdict === verdict).length;
  return { ...state, leftovers, summary: { total: leftovers.length, safeToRemove: of("SafeToRemove"), needsReview: of("NeedsReview"), inUse: of("InUse") } };
}

function planOf(selected: SelectedLeftover, leftover: Leftover | null): LeftoverPlanView {
  const base = { kind: selected.kind, name: selected.name, path: selected.path };
  if (leftover === null) return { ...base, refused: "It is no longer a leftover. Refresh to see the repo as it is now.", steps: [] };
  if (leftover.verdict === "InUse") return { ...base, refused: "In use, so it is never cleaned up.", steps: [] };
  return { ...base, refused: null, steps: stepsOf(leftover) };
}

const step = (kind: StepView["kind"], description: string, over: Partial<StepView> = {}): StepView => ({
  kind,
  description,
  undoable: true,
  undoNote: null,
  optional: false,
  ...over,
});

function backup(leftover: Leftover): StepView[] {
  const commits = leftover.commitsAtRisk ?? 0;
  if (commits === 0) return [];
  const what = commits === 1 ? "1 commit" : `${commits} commits`;
  return [step("Backup", `Keep a backup tag archive/branch/${leftover.name}-2026-10-21 for ${what} that exist nowhere else`)];
}

function stepsOf(leftover: Leftover): StepView[] {
  switch (leftover.kind) {
    case "LeftoverFolder":
      return [step("RecycleFolder", `Move folder ${leftover.path} to the Recycle Bin`, { undoable: false, undoNote: "Restore it from the Recycle Bin." })];
    case "Worktree":
      return [...backup(leftover), step("RemoveWorktree", `Remove worktree ${leftover.path}`), step("DeleteBranch", `Delete branch ${leftover.name}`, { optional: true })];
    case "LocalBranch":
      return [...backup(leftover), step("DeleteBranch", `Delete branch ${leftover.name}`)];
    default:
      return [step("DeleteRefs", `Delete ${leftover.name}`, { undoNote: "Undo works while git still has the commits." })];
  }
}
