import { useLayoutEffect, useRef } from "react";
import { drawGraph, graphMetrics, type GraphColors, type GraphRow } from "./drawGraph";
import type { RowRange } from "./viewport";

interface Props {
  row(index: number): GraphRow;
  range: RowRange;
  /** In CSS pixels. */
  width: number;
  colors: GraphColors;
  /** Changes whenever the rows do, so the canvas redraws when a page arrives. */
  version: number;
}

/** The graph for the rows in `range`, placed where those rows are. Sharp on high-DPI screens. */
export function GraphCanvas({ row, range, width, colors, version }: Props) {
  const ref = useRef<HTMLCanvasElement>(null);
  const height = (range.end - range.first) * graphMetrics.rowHeight;

  useLayoutEffect(() => {
    const canvas = ref.current;
    const ctx = canvas?.getContext("2d");
    if (!canvas || !ctx) return;
    const scale = window.devicePixelRatio || 1;
    canvas.width = Math.round(width * scale);
    canvas.height = Math.round(height * scale);
    ctx.setTransform(scale, 0, 0, scale, 0, 0);
    drawGraph(ctx, row, range, { colors });
  }, [row, range, width, height, colors, version]);

  return <canvas ref={ref} className="graph" aria-hidden="true" style={{ top: range.first * graphMetrics.rowHeight, width, height }} />;
}
