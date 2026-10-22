/** The rows to draw: `first` up to but not including `end`. */
export interface RowRange {
  first: number;
  end: number;
}

/** Rows drawn above and below the visible ones, so a small scroll never shows an empty band. */
export const Overscan = 10;

/** Where a scrolling element stands: its scroll position and visible height, in pixels. */
export interface Viewport {
  scrollTop: number;
  height: number;
}

/** The rows inside the viewport, plus `overscan` on each side, clamped to the rows that exist. */
export function visibleRange({ scrollTop, height }: Viewport, rowHeight: number, count: number, overscan = Overscan): RowRange {
  const top = Math.floor(scrollTop / rowHeight);
  const bottom = Math.ceil((scrollTop + height) / rowHeight);
  return { first: clamp(top - overscan, 0, count), end: clamp(bottom + overscan, 0, count) };
}

/** The scroll position that shows row `index` whole, moving as little as possible. */
export function scrollToShow(index: number, scrollTop: number, viewportHeight: number, rowHeight: number): number {
  const top = index * rowHeight;
  if (top < scrollTop) return top;
  const bottom = top + rowHeight;
  if (bottom > scrollTop + viewportHeight) return Math.max(0, bottom - viewportHeight);
  return scrollTop;
}

/** The keys that move the selection, and how far. */
const moves: Record<string, (current: number, page: number, last: number) => number> = {
  ArrowUp: (current) => current - 1,
  ArrowDown: (current) => current + 1,
  PageUp: (current, page) => current - page,
  PageDown: (current, page) => current + page,
  Home: () => 0,
  End: (_current, _page, last) => last,
};

/**
 * The row selected after pressing `key`, or null when the key does not move the selection.
 * With nothing selected (-1), any move selects the first row, except End.
 */
export function moveSelection(key: string, current: number, pageRows: number, count: number): number | null {
  const move = moves[key];
  if (!move || count === 0) return null;
  if (current < 0 && key !== "End") return 0;
  return clamp(move(current, Math.max(1, pageRows), count - 1), 0, count - 1);
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max);
}
