import type { LaneLine, LaneRow } from "./lanes";
import type { RowRange } from "./viewport";

/** The part of a 2D canvas context the graph uses, so tests can record the calls without a real canvas. */
export type GraphContext = Pick<
  CanvasRenderingContext2D,
  "beginPath" | "moveTo" | "lineTo" | "bezierCurveTo" | "arc" | "stroke" | "fill" | "clearRect" | "strokeStyle" | "fillStyle" | "lineWidth" | "globalAlpha"
>;

export interface GraphMetrics {
  rowHeight: number;
  laneWidth: number;
  dotRadius: number;
}

export const graphMetrics: GraphMetrics = { rowHeight: 28, laneWidth: 16, dotRadius: 4 };

/** Lanes beyond this are cut off, so a repo with many parallel branches still leaves room for the subjects. */
export const MaxLanes = 24;

/** The graph's width in CSS pixels for a layout this many lanes wide. */
export function graphWidth(lanes: number, metrics: GraphMetrics = graphMetrics): number {
  return (Math.min(Math.max(lanes, 1), MaxLanes) + 1) * metrics.laneWidth;
}

export interface GraphColors {
  /** Lane colours; a lane's colour index picks one with `color % lanes.length`. */
  lanes: readonly string[];
  /** Fills a merge commit's dot, so it reads as a ring. */
  background: string;
}

/** How the graph looks. `metrics` defaults to graphMetrics. */
export interface GraphStyle {
  colors: GraphColors;
  metrics?: GraphMetrics;
}

/** One row to draw: its layout, whether its commit is a merge, and whether a toggle fades it. */
export interface GraphRow {
  layout: LaneRow;
  isMerge: boolean;
  faded?: boolean;
}

/** How opaque a faded row's dot and lines are. Lines that only pass through the row keep their own branch's look. */
export const FadedAlpha = 0.35;

/**
 * Draws the rows in `range` with the first one at the top of the canvas. Each row draws only the lines inside its
 * own band, which is what lets the list draw just the rows in view.
 */
export function drawGraph(ctx: GraphContext, rows: (index: number) => GraphRow, range: RowRange, style: GraphStyle): void {
  const { colors, metrics = graphMetrics } = style;
  const painter = new Painter(ctx, colors, metrics);
  ctx.clearRect(0, 0, graphWidth(MaxLanes, metrics), (range.end - range.first) * metrics.rowHeight);
  for (let i = range.first; i < range.end; i++) painter.row(rows(i), (i - range.first) * metrics.rowHeight);
}

class Painter {
  private readonly ctx: GraphContext;
  private readonly colors: GraphColors;
  private readonly metrics: GraphMetrics;

  constructor(ctx: GraphContext, colors: GraphColors, metrics: GraphMetrics) {
    this.ctx = ctx;
    this.colors = colors;
    this.metrics = metrics;
  }

  row({ layout, isMerge, faded = false }: GraphRow, top: number): void {
    this.ctx.lineWidth = 2;
    for (const line of layout.lines) {
      this.ctx.globalAlpha = faded && line.part !== "through" ? FadedAlpha : 1;
      this.line(line, top);
    }
    this.ctx.globalAlpha = faded ? FadedAlpha : 1;
    this.dot(layout, isMerge, top);
    this.ctx.globalAlpha = 1;
  }

  private line({ part, from, to, color }: LaneLine, top: number): void {
    const { rowHeight } = this.metrics;
    const [startY, endY] = part === "through" ? [top, top + rowHeight] : part === "upper" ? [top, top + rowHeight / 2] : [top + rowHeight / 2, top + rowHeight];
    const startX = this.x(from);
    const endX = this.x(to);
    const ctx = this.ctx;
    ctx.strokeStyle = this.color(color);
    ctx.beginPath();
    ctx.moveTo(startX, startY);
    // Straight down the lane, or an S-bend that leaves and arrives vertically.
    if (startX === endX) ctx.lineTo(endX, endY);
    else ctx.bezierCurveTo(startX, endY, endX, startY, endX, endY);
    ctx.stroke();
  }

  private dot({ lane, color }: LaneRow, isMerge: boolean, top: number): void {
    const ctx = this.ctx;
    ctx.beginPath();
    ctx.arc(this.x(lane), top + this.metrics.rowHeight / 2, this.metrics.dotRadius + (isMerge ? 1 : 0), 0, Math.PI * 2);
    ctx.fillStyle = isMerge ? this.colors.background : this.color(color);
    ctx.fill();
    ctx.strokeStyle = this.color(color);
    ctx.stroke();
  }

  private x(lane: number): number {
    // Lanes past MaxLanes land outside the canvas, which clips them.
    return (lane + 1) * this.metrics.laneWidth;
  }

  private color(index: number): string {
    return this.colors.lanes[index % this.colors.lanes.length]!;
  }
}
