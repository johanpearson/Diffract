import { describe, expect, it } from "vitest";
import { emptyLaneState, layoutLanes, type LaneCommit, type LaneLine, type LaneRow } from "./lanes";

/** Commits from lines like "M: A F", meaning commit M with parents A then F. Listed children first. */
function graph(...lines: string[]): LaneCommit[] {
  return lines.map((line) => {
    const [id, parents = ""] = line.split(":").map((s) => s.trim());
    return { id: id!, parentIds: parents.split(/\s+/).filter(Boolean) };
  });
}

/** Each row as "lane: lines", lines as "u1>0" (upper from 1 to 0), "t2" (through 2) and "l0>1" (lower from 0 to 1). */
function describeRows(rows: LaneRow[]): string[] {
  const line = (l: LaneLine) => (l.part === "through" ? `t${l.from}` : `${l.part[0]}${l.from}>${l.to}`);
  return rows.map((r) => `${r.lane}: ${r.lines.map(line).join(" ")}`.trimEnd());
}

const leaving = (row: LaneRow) => row.lines.filter((l) => l.part !== "upper");
const entering = (row: LaneRow) => row.lines.filter((l) => l.part !== "lower");

/** A row with its position, for messages that say where a check failed. */
interface NumberedRow {
  row: LaneRow;
  index: number;
}

/** No two lines enter one column, and two lines leave one column only where a merge line joins a lane to that parent. */
function expectNoSharedColumns({ row, index }: NumberedRow) {
  const tops = entering(row).map((l) => l.from);
  expect(new Set(tops).size, `row ${index} has two lines entering one column`).toBe(tops.length);

  const out = leaving(row);
  const joins = out.filter((l) => l.part === "lower" && out.some((t) => t.part === "through" && t.to === l.to)).length;
  expect(new Set(out.map((l) => l.to)).size + joins, `row ${index} has two lines leaving one column`).toBe(out.length);
}

/** The columns entering a row are exactly the columns leaving the row above. */
function expectContinues(above: LaneRow, { row, index }: NumberedRow) {
  const columns = (lines: LaneLine[], end: "from" | "to") => [...new Set(lines.map((l) => l[end]))].sort();
  expect(columns(entering(row), "from"), `row ${index} does not continue the row above`).toEqual(columns(leaving(above), "to"));
}

/** One line from a commit's dot to a parent's: it leaves `fromRow` in `column` and should arrive in `toRow`. */
interface ParentLine {
  name: string;
  fromRow: number;
  column: number;
  /** Past the last row when the parent is not loaded, so the line must run off the bottom. */
  toRow: number;
}

/** The line runs straight down its column and reaches the parent's dot, or the bottom if the parent is not loaded. */
function expectReaches(rows: LaneRow[], line: ParentLine) {
  for (let below = line.fromRow + 1; below < Math.min(line.toRow, rows.length); below++) {
    expect(rows[below]!.lines.some((l) => l.part === "through" && l.from === line.column), `${line.name} breaks at row ${below}`).toBe(true);
  }
  const target = rows[line.toRow];
  if (target) expect(target.lines.some((l) => l.part === "upper" && l.from === line.column && l.to === target.lane), `${line.name} misses its dot`).toBe(true);
}

/**
 * Checks what the renderer relies on: every line from a commit to a parent goes straight down one column and arrives
 * at that parent's dot, or runs off the bottom when the parent is not loaded. Lines never share a column by accident.
 */
function expectConnected(commits: readonly LaneCommit[], rows: LaneRow[]) {
  rows.forEach((row, index) => {
    expectNoSharedColumns({ row, index });
    if (index > 0) expectContinues(rows[index - 1]!, { row, index });
  });

  const rowOf = new Map(commits.map((c, i) => [c.id, i]));
  commits.forEach((commit, fromRow) => {
    const lowers = rows[fromRow]!.lines.filter((l) => l.part === "lower");
    expect(lowers.every((l) => l.from === rows[fromRow]!.lane)).toBe(true);
    commit.parentIds.forEach((parent, k) =>
      expectReaches(rows, { name: `${commit.id} to ${parent}`, fromRow, column: lowers[k]!.to, toRow: rowOf.get(parent) ?? rows.length }));
  });
}

function layout(commits: LaneCommit[]): LaneRow[] {
  const { rows } = layoutLanes(commits);
  expectConnected(commits, rows);
  return rows;
}

interface HistoryShape {
  count: number;
  /** The same seed always gives the same history. */
  seed?: number;
  /** At most this many feature branches are open at once. */
  maxBranches?: number;
}

/** What the next commit does: which branch it moves, its parents, and the branch it merges and closes, if any. */
interface Step {
  branch: string;
  parentIds: string[];
  closes?: string;
}

/** Grows a history oldest first: main, with feature branches that start from it, grow and merge back. */
class HistoryGrower {
  readonly oldestFirst: LaneCommit[] = [];
  private readonly tips = new Map<string, string>();
  private nextBranch = 0;
  private state: number;

  constructor(private readonly shape: HistoryShape) {
    this.state = shape.seed ?? 1;
  }

  grow() {
    const id = `c${this.oldestFirst.length}`;
    const step = this.nextStep();
    this.oldestFirst.push({ id, parentIds: step.parentIds });
    this.tips.set(step.branch, id);
    if (step.closes) this.tips.delete(step.closes);
  }

  private nextStep(): Step {
    const roll = this.random();
    const open = [...this.tips.keys()].filter((b) => b !== "main");
    const main = this.tips.get("main");
    if (main === undefined) return { branch: "main", parentIds: [] };
    if (roll < 0.12 && open.length < (this.shape.maxBranches ?? 6)) return { branch: `f${this.nextBranch++}`, parentIds: [main] };
    if (roll < 0.22 && open.length > 0) {
      const merged = this.pick(open);
      return { branch: "main", parentIds: [main, this.tips.get(merged)!], closes: merged };
    }
    const branch = open.length > 0 && roll > 0.5 ? this.pick(open) : "main";
    return { branch, parentIds: [this.tips.get(branch)!] };
  }

  private pick(branches: string[]): string {
    return branches[Math.floor(this.random() * branches.length)]!;
  }

  /** A small linear congruential generator, so a seed always gives the same history. */
  private random(): number {
    this.state = (this.state * 1103515245 + 12345) % 2147483648;
    return this.state / 2147483648;
  }
}

/** A history shaped like the 50,000-commit test repo, in topological order: built oldest first, then reversed. */
function generatedHistory(shape: HistoryShape): LaneCommit[] {
  const grower = new HistoryGrower(shape);
  for (let n = 0; n < shape.count; n++) grower.grow();
  return grower.oldestFirst.reverse();
}

describe("lane layout", () => {
  it("puts a linear history in one lane and colour", () => {
    const rows = layout(graph("C: B", "B: A", "A:"));

    expect(describeRows(rows)).toEqual(["0: l0>0", "0: u0>0 l0>0", "0: u0>0"]);
    expect(new Set(rows.map((r) => r.color))).toEqual(new Set([rows[0]!.color]));
  });

  it("gives a branch its own lane and colour, and brings it back at the merge", () => {
    const rows = layout(graph("M: B F", "F: B", "B: A", "A:"));

    expect(describeRows(rows)).toEqual([
      "0: l0>0 l0>1",
      "1: t0 u1>1 l1>1",
      "0: u0>0 u1>0 l0>0",
      "0: u0>0",
    ]);
    expect(rows[1]!.color).not.toBe(rows[0]!.color);
    expect(rows[2]!.color).toBe(rows[0]!.color);
  });

  it("draws a merge line in the colour of the lane it joins", () => {
    const rows = layout(graph("M: B F", "F: B", "B:"));

    const toBranch = rows[0]!.lines.find((l) => l.part === "lower" && l.to === 1)!;
    expect(toBranch.color).toBe(rows[1]!.color);
  });

  it("keeps a branch that started earlier in its own lane until its fork point", () => {
    // Two tips, feature first in the list: feature keeps lane 0 and main takes lane 1, both straight.
    const rows = layout(graph("F2: F1", "M2: M1", "F1: M1", "M1: A", "A:"));

    expect(rows.map((r) => r.lane)).toEqual([0, 1, 0, 0, 0]);
    expect(describeRows(rows)[3]).toBe("0: u0>0 u1>0 l0>0");
  });

  it("lays out an octopus merge with a lane for each parent", () => {
    const rows = layout(graph("O: A B C", "C: Z", "B: Z", "A: Z", "Z:"));

    expect(describeRows(rows)[0]).toBe("0: l0>0 l0>1 l0>2");
    expect(rows.slice(1, 4).map((r) => r.lane)).toEqual([2, 1, 0]);
    expect(describeRows(rows)[4]).toBe("0: u0>0 u1>0 u2>0");
  });

  it("lays out criss-cross merges, where two merges share the same two parents", () => {
    const rows = layout(graph("M1: A B", "M2: B A", "A: O", "B: O", "O:"));

    expect(rows.map((r) => r.lane)).toEqual([0, 2, 0, 1, 0]);
    expect(Math.max(...rows.map((r) => r.lines.length))).toBeLessThanOrEqual(5);
  });

  it("gives each of many parallel branches its own lane", () => {
    const tips = Array.from({ length: 20 }, (_, i) => `T${i}: B`);
    const rows = layout(graph(...tips, "B:"));

    expect(rows.slice(0, 20).map((r) => r.lane)).toEqual(Array.from({ length: 20 }, (_, i) => i));
    expect(rows[20]!.lines.filter((l) => l.part === "upper")).toHaveLength(20);
    expect(new Set(rows.slice(0, 20).map((r) => r.color)).size).toBe(20);
    expect(layoutLanes(graph(...tips, "B:")).state.width).toBe(20);
  });

  it("reuses a lane once its branch has ended", () => {
    const rows = layout(graph("M: A F", "F: A", "A: B", "G: B", "B:"));

    // F's lane is free after A, so the new tip G takes lane 1 again instead of a third lane.
    expect(rows[3]!.lane).toBe(1);
    expect(layoutLanes(graph("M: A F", "F: A", "A: B", "G: B", "B:")).state.width).toBe(2);
  });

  it("keeps a lane open when the parent is not loaded yet, and joins it when it arrives", () => {
    const commits = graph("M: A F", "F: X", "A: X", "X:");
    const firstPage = layoutLanes(commits.slice(0, 3));

    // F's parent X is not in the first page, so its lane runs off the bottom.
    expect(firstPage.state.lanes).toEqual(["X", "X"]);
    expect(describeRows(firstPage.rows)[2]).toBe("0: u0>0 t1 l0>0");

    const secondPage = layoutLanes(commits.slice(3), firstPage.state);
    expect(describeRows(secondPage.rows)).toEqual(["0: u0>0 u1>0"]);
    expect(secondPage.state.lanes).toEqual([]);
  });

  it("gives the same rows page by page as all at once", () => {
    const commits = generatedHistory({ count: 5000, seed: 7 });
    const all = layoutLanes(commits);

    let state = emptyLaneState;
    const paged: LaneRow[] = [];
    for (let start = 0; start < commits.length; start += 500) {
      const page = layoutLanes(commits.slice(start, start + 500), state);
      paged.push(...page.rows);
      state = page.state;
    }

    expect(paged).toEqual(all.rows);
    expect(state).toEqual(all.state);
  });

  it("connects every commit to every parent in a generated branchy history", () => {
    const commits = generatedHistory({ count: 3000, seed: 3 });

    const rows = layout(commits);

    expect(layoutLanes(commits).state.width).toBeLessThanOrEqual(12);
    expect(rows.some((r) => r.lane > 0)).toBe(true);
  });

  it("does not change the state it was given", () => {
    const first = layoutLanes(graph("F: A", "M: A"));
    const copy = structuredClone(first.state);

    layoutLanes(graph("A:"), first.state);

    expect(first.state).toEqual(copy);
  });

  it("handles an empty page", () => {
    expect(layoutLanes([])).toEqual({ rows: [], state: emptyLaneState });
  });

  it("lays out 50,000 commits within the phase 0 budget of 300 ms", () => {
    const commits = generatedHistory({ count: 50_000 });

    const started = performance.now();
    const { rows } = layoutLanes(commits);
    const elapsed = performance.now() - started;

    expect(rows).toHaveLength(50_000);
    expect(elapsed).toBeLessThan(300);
  });
});
