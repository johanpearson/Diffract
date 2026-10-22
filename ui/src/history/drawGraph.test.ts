import { describe, expect, it } from "vitest";
import { drawGraph, graphWidth, MaxLanes, type GraphContext, type GraphMetrics, type GraphRow } from "./drawGraph";
import { layoutLanes, type LaneCommit } from "./lanes";

const metrics: GraphMetrics = { rowHeight: 20, laneWidth: 10, dotRadius: 3 };
const colors = { lanes: ["red", "blue"], background: "black" };

/** A canvas context that writes down what was drawn, one string per stroke or fill. */
function recorder() {
  const drawn: string[] = [];
  let path: string[] = [];
  const ctx: GraphContext = {
    strokeStyle: "",
    fillStyle: "",
    lineWidth: 1,
    clearRect: () => {},
    beginPath: () => void (path = []),
    moveTo: (x, y) => void path.push(`M${x},${y}`),
    lineTo: (x, y) => void path.push(`L${x},${y}`),
    bezierCurveTo: (_a, _b, _c, _d, x, y) => void path.push(`C${x},${y}`),
    arc: (x, y, r) => void path.push(`O${x},${y},${r}`),
    stroke: () => void drawn.push(`stroke ${String(ctx.strokeStyle)} ${path.join(" ")}`),
    fill: () => void drawn.push(`fill ${String(ctx.fillStyle)} ${path.join(" ")}`),
  };
  return { ctx, drawn };
}

function rowsOf(commits: LaneCommit[]): (i: number) => GraphRow {
  const { rows } = layoutLanes(commits);
  return (i) => ({ layout: rows[i]!, isMerge: commits[i]!.parentIds.length > 1 });
}

describe("drawGraph", () => {
  it("draws a straight line between commits on one lane and a dot for each", () => {
    const { ctx, drawn } = recorder();

    drawGraph(ctx, rowsOf([{ id: "b", parentIds: ["a"] }, { id: "a", parentIds: [] }]), { first: 0, end: 2 }, { colors, metrics });

    expect(drawn).toEqual([
      "stroke red M10,10 L10,20",
      "fill red O10,10,3",
      "stroke red O10,10,3",
      "stroke red M10,20 L10,30",
      "fill red O10,30,3",
      "stroke red O10,30,3",
    ]);
  });

  it("draws a merge as a ring with a bend to its second parent's lane", () => {
    const { ctx, drawn } = recorder();
    const commits = [
      { id: "m", parentIds: ["a", "b"] },
      { id: "b", parentIds: ["a"] },
      { id: "a", parentIds: [] },
    ];

    drawGraph(ctx, rowsOf(commits), { first: 0, end: 1 }, { colors, metrics });

    expect(drawn).toContain("stroke blue M10,10 C20,20");
    expect(drawn).toContain("fill black O10,10,4");
  });

  it("draws only the rows in the range, the first one at the top", () => {
    const { ctx, drawn } = recorder();
    const commits = Array.from({ length: 100 }, (_, i) => ({ id: `c${i}`, parentIds: i < 99 ? [`c${i + 1}`] : [] }));

    drawGraph(ctx, rowsOf(commits), { first: 50, end: 52 }, { colors, metrics });

    expect(drawn.filter((d) => d.startsWith("fill"))).toEqual(["fill red O10,10,3", "fill red O10,30,3"]);
  });
});

describe("graphWidth", () => {
  it("leaves one lane of margin", () => {
    expect(graphWidth(3, metrics)).toBe(40);
  });

  it("is never wider than the lane limit", () => {
    expect(graphWidth(500, metrics)).toBe(graphWidth(MaxLanes, metrics));
  });
});
