import { useEffect, useState, type RefObject } from "react";
import type { GraphColors } from "./drawGraph";

/** How many lane colours styles.css defines, as --lane-0 and up. */
export const LaneColorCount = 5;

/** The lane colours as CSS can use them. */
export function laneColor(index: number): string {
  return `var(--lane-${index % LaneColorCount})`;
}

// Used until styles are computed, for example in tests.
const fallback: GraphColors = { lanes: ["#8fb8a0", "#6aa6f0", "#e8a33d", "#b48cf2", "#4fc6b8"], background: "#0c1013" };

/** The lane colours of the current theme, for the canvas, which cannot use CSS variables. Follows a theme change. */
export function useGraphColors(ref: RefObject<HTMLElement | null>): GraphColors {
  const [colors, setColors] = useState<GraphColors>(fallback);

  useEffect(() => {
    const element = ref.current;
    if (!element) return;
    const read = () => setColors(readColors(getComputedStyle(element)));
    read();
    const query = typeof matchMedia === "function" ? matchMedia("(prefers-color-scheme: dark)") : null;
    query?.addEventListener("change", read);
    return () => query?.removeEventListener("change", read);
  }, [ref]);

  return colors;
}

function readColors(style: CSSStyleDeclaration): GraphColors {
  const value = (name: string, otherwise: string) => style.getPropertyValue(name).trim() || otherwise;
  return {
    lanes: fallback.lanes.map((color, i) => value(`--lane-${i}`, color)),
    background: value("--bg", fallback.background),
  };
}
