import { describe, expect, it } from "vitest";
import applySample from "../../../tests/Diffract.Sidecar.Tests/Contract/cleanup.apply.json";
import planSample from "../../../tests/Diffract.Sidecar.Tests/Contract/cleanup.plan.json";
import type { CleanupPlanView, CleanupResult, Leftover } from "../protocol";
import { initialRows, type Row } from "../model/rows";
import { sampleState } from "../testing/fakeSidecar";
import { keepsOnly, leaveOutOf, needsAttention, outcomeLine, plannedCount, stepKey, summarize } from "./outcome";
import { isMergedBranch, selectable, selectedRows, toSelected, withMergedBranches } from "./selection";

// Written by the sidecar's contract tests, from its own serializer.
const plan = (planSample as { result: CleanupPlanView }).result;
const applied = (applySample as { result: CleanupResult }).result;

const leftover = (over: Partial<Leftover>): Leftover => ({
  kind: "LocalBranch",
  name: "topic",
  path: null,
  agent: null,
  verdict: "SafeToRemove",
  commitsAtRisk: 0,
  reason: "Merged into main",
  ...over,
});

const rows = (...leftovers: Leftover[]): Row[] => initialRows(leftovers);

describe("selection", () => {
  it("offers every row but those in use, and those fading out", () => {
    const [safe, review, inUse] = initialRows(sampleState.leftovers);

    expect([safe, review, inUse].map((row) => selectable(row!))).toEqual([true, true, false]);
    expect(selectable({ ...safe!, flash: "removed" })).toBe(false);
  });

  it("takes merged branches to be the local branches that are safe to remove", () => {
    expect(isMergedBranch(leftover({}))).toBe(true);
    expect(isMergedBranch(leftover({ verdict: "NeedsReview" }))).toBe(false);
    expect(isMergedBranch(leftover({ kind: "RemoteBranch" }))).toBe(false);
  });

  it("selects all merged branches, keeps what was already ticked, and nothing else", () => {
    const list = rows(
      leftover({ name: "merged" }),
      leftover({ name: "unmerged", verdict: "NeedsReview" }),
      leftover({ name: "folder", kind: "LeftoverFolder", path: "C:\\x" }),
      leftover({ name: "busy", verdict: "InUse" }),
    );

    const selected = withMergedBranches(list, new Set([list[2]!.key]));

    expect(list.filter((row) => selected.has(row.key)).map((row) => row.leftover.name)).toEqual(["merged", "folder"]);
  });

  it("drops a ticked row that went into use or went away", () => {
    const list = rows(leftover({ name: "a" }), leftover({ name: "b" }), leftover({ name: "c" }));
    const ticked = new Set(list.map((row) => row.key));
    const now = [list[0]!, { ...list[1]!, leftover: { ...list[1]!.leftover, verdict: "InUse" as const } }];

    expect(selectedRows(now, ticked).map((row) => row.leftover.name)).toEqual(["a"]);
  });

  it("names a leftover by its kind, name and path", () => {
    expect(toSelected(sampleState.leftovers[0]!)).toEqual({ kind: "LeftoverFolder", name: "old-task", path: "C:\\src\\shop\\.claude\\worktrees\\old-task" });
  });
});

describe("outcome", () => {
  it("leaves out only the optional steps that were unticked", () => {
    const unticked = new Set([stepKey({ leftover: 0, step: 2 }), stepKey({ leftover: 0, step: 1 })]);

    expect(leaveOutOf(plan, unticked)).toEqual([{ leftover: 0, step: 2 }]);
  });

  it("counts the leftovers with steps, not the refused ones", () => {
    expect(plannedCount(plan)).toBe(2);
  });

  it("words a plan as keeping only when every planned leftover is orphaned commits", () => {
    const orphaned = { kind: "OrphanedCommit" as const, name: "2 commits", path: null, refused: null, steps: [] };

    expect(keepsOnly({ ...plan, leftovers: [orphaned, plan.leftovers[2]!] })).toBe(true);
    expect(keepsOnly({ ...plan, leftovers: [orphaned, ...plan.leftovers] })).toBe(false);
    expect(keepsOnly({ ...plan, leftovers: [plan.leftovers[2]!] })).toBe(false);
  });

  it("counts a keep step as a backup kept, not as something removed", () => {
    const keep = { leftover: 0, step: 0, kind: "KeepCommits" as const, description: "Keep 2 commits", outcome: "Done" as const, reason: null };

    const summary = summarize({ cleanupId: "x", steps: [keep] }, false);

    expect([summary.done.length, summary.backups.length]).toEqual([0, 1]);
  });

  it("groups what a cleanup did: removed, kept as backups, and not done", () => {
    const summary = summarize(applied, false);

    expect(summary.done.map((s) => s.kind)).toEqual(["RemoveWorktree", "DeleteBranch"]);
    expect(summary.backups.map((s) => s.kind)).toEqual(["Backup"]);
    expect(summary.notDone.map((s) => `${s.kind} ${s.outcome}`)).toEqual(["RecycleFolder Failed"]);
    expect(outcomeLine(summary, false)).toBe("2 steps done. Kept 1 backup tag. 1 step not done: see the result.");
  });

  it("counts an undo's backup tag going as one more step undone", () => {
    const summary = summarize({ ...applied, steps: applied.steps.slice(0, 1) }, true);

    expect([summary.done.length, summary.backups.length]).toEqual([1, 0]);
    expect(outcomeLine(summary, true)).toBe("1 step undone.");
  });

  it("needs attention when a step failed or was skipped, but not for a step the user left out", () => {
    const leftOut = { ...applied.steps[2]!, outcome: "Skipped" as const, reason: "Left out, as chosen." };
    const done = { ...applied, steps: [applied.steps[0]!, leftOut] };

    expect(needsAttention(applied, [])).toBe(true);
    expect(needsAttention(done, [{ leftover: 0, step: 2 }])).toBe(false);
    expect(needsAttention(done, [])).toBe(true);
  });
});
