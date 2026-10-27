import { describe, expect, it } from "vitest";
import type { Leftover } from "../protocol";
import { initialRows, leftoverKey, settleRows, updateRows } from "./rows";

const item = (name: string, overrides: Partial<Leftover> = {}): Leftover => ({
  kind: "LocalBranch",
  name,
  path: null,
  agent: null,
  verdict: "NeedsReview",
  commitsAtRisk: 1,
  reason: "Reason",
  check: null,
  commit: null,
  ...overrides,
});

const flashes = (rows: ReturnType<typeof updateRows>) => rows.map((r) => `${r.leftover.name}:${r.flash ?? "-"}`);

describe("leftoverKey", () => {
  it("tells apart rows that differ only by agent, as the sidecar does", () => {
    const claude = item("1 commit", { kind: "OrphanedCommit", agent: "Claude Code" });
    const copilot = item("1 commit", { kind: "OrphanedCommit", agent: "GitHub Copilot" });

    expect(leftoverKey(claude)).not.toBe(leftoverKey(copilot));
  });

  it("uses the path when there is one, so a renamed branch in the same worktree is the same row", () => {
    const before = item("claude/a", { kind: "Worktree", path: "C:\\src\\wt" });
    const after = item("claude/b", { kind: "Worktree", path: "C:\\src\\wt" });

    expect(leftoverKey(before)).toBe(leftoverKey(after));
  });
});

describe("updateRows", () => {
  it("marks new rows added and rows with another verdict changed", () => {
    const rows = initialRows([item("a"), item("b")]);

    const next = updateRows(rows, [item("a"), item("b", { verdict: "InUse" }), item("c")]);

    expect(flashes(next)).toEqual(["a:-", "b:changed", "c:added"]);
  });

  it("keeps a removed row where it was until it settles", () => {
    const rows = initialRows([item("a"), item("b"), item("c")]);

    const next = updateRows(rows, [item("a"), item("c")]);

    expect(flashes(next)).toEqual(["a:-", "b:removed", "c:-"]);
    expect(flashes(settleRows(next))).toEqual(["a:-", "c:-"]);
  });

  it("keeps a highlight that has not settled yet through the next update", () => {
    const first = updateRows(initialRows([item("a")]), [item("a"), item("b")]);

    const second = updateRows(first, [item("a", { reason: "Other reason" }), item("b")]);

    expect(flashes(second)).toEqual(["a:-", "b:added"]);
  });

  it("brings back a row that was removed and came back before it settled as added", () => {
    const removed = updateRows(initialRows([item("a")]), []);

    expect(flashes(updateRows(removed, [item("a")]))).toEqual(["a:added"]);
  });

  it("does not remove a removed row twice", () => {
    const removed = updateRows(initialRows([item("a"), item("b")]), [item("a")]);

    expect(flashes(updateRows(removed, [item("a")]))).toEqual(["a:-", "b:removed"]);
  });

  it("shows the newest data in a row", () => {
    const next = updateRows(initialRows([item("a")]), [item("a", { commitsAtRisk: 5 })]);

    expect(next[0]?.leftover.commitsAtRisk).toBe(5);
  });
});
