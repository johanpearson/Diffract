import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, type KeyboardEvent, type MouseEvent, type RefObject } from "react";
import type { CommitEntry } from "../protocol";
import { formatAge } from "../model/format";
import { graphMetrics, graphWidth, type GraphRow } from "./drawGraph";
import { GraphCanvas } from "./GraphCanvas";
import type { Marker } from "./markers";
import { MarkerChip } from "./MarkerChip";
import { laneColor, useGraphColors } from "./useGraphColors";
import type { History } from "./useHistory";
import { useViewport } from "./useViewport";
import { anchoredScrollTop, anchorAt, moveSelection, scrollToShow, visibleRange, type ScrollAnchor } from "./viewport";

interface Props {
  history: History;
  markers: ReadonlyMap<string, Marker[]>;
  /** The commits a toggle fades. */
  faded: ReadonlySet<string>;
  /** The selected row, or -1. */
  selected: number;
  onSelect(index: number): void;
  /** Enter: move to the details. */
  onOpen(): void;
  /** A right-click on a row, or the menu key or Shift+F10 on the list: open the row's menu at this point. */
  onMenu(index: number, at: { x: number; y: number }): void;
  now: Date;
}

const { rowHeight } = graphMetrics;

/** Every commit, one row each, with the graph on the left. Only the rows in view are drawn. */
export function HistoryList({ history, markers, faded, selected, onSelect, onOpen, onMenu, now }: Props) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const viewport = useViewport(scrollRef);
  const { scrollTop, height } = viewport;
  const colors = useGraphColors(scrollRef);
  const { commits, rows } = history;
  const range = useMemo(() => visibleRange({ scrollTop, height }, rowHeight, commits.length), [scrollTop, height, commits.length]);
  const width = graphWidth(history.width);
  const row = useCallback(
    (i: number): GraphRow => ({ layout: rows[i]!, isMerge: commits[i]!.parentIds.length > 1, faded: faded.has(commits[i]!.id) }),
    [rows, commits, faded],
  );
  const onScroll = useScrollAnchor(scrollRef, history, viewport.onScroll);
  useSelectionInView(scrollRef, { index: selected, id: commits[selected]?.id }, height, onScroll);

  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === "Enter") return onOpen();
    const next = moveSelection(event.key, selected, Math.floor(height / rowHeight) - 1, commits.length);
    if (next === null) return;
    event.preventDefault();
    onSelect(next);
  };

  const onContextMenu = (event: MouseEvent<HTMLDivElement>) => {
    const row = (event.target as HTMLElement).closest<HTMLElement>("[data-index]");
    const index = row ? Number(row.dataset.index) : selected;
    if (index < 0) return;
    event.preventDefault();
    if (index !== selected) onSelect(index);
    onMenu(index, menuPoint(event, row ?? document.getElementById(optionId(index))));
  };

  const shown = commits.slice(range.first, range.end);
  return (
    <div
      ref={scrollRef}
      className="history-scroll"
      role="listbox"
      aria-label="Commits"
      tabIndex={0}
      aria-activedescendant={selected >= 0 ? optionId(selected) : undefined}
      onScroll={onScroll}
      onKeyDown={onKeyDown}
      onContextMenu={onContextMenu}
    >
      <div className="history-rows" role="presentation" style={{ height: commits.length * rowHeight }}>
        <GraphCanvas row={row} range={range} width={width} colors={colors} version={history.version} />
        {shown.map((commit, offset) => {
          const index = range.first + offset;
          return (
            <CommitRow
              key={commit.id}
              commit={commit}
              index={index}
              count={commits.length}
              color={laneColor(rows[index]!.color)}
              markers={markers.get(commit.id)}
              faded={faded.has(commit.id)}
              selected={index === selected}
              indent={width}
              onSelect={onSelect}
              now={now}
            />
          );
        })}
      </div>
    </div>
  );
}

/**
 * Keeps the commits in view where they are when commits are added or removed above them. At the very top, new
 * commits come into view instead. Returns the scroll handler, which also remembers where the view is.
 */
function useScrollAnchor(scrollRef: RefObject<HTMLElement | null>, { commits, version }: History, onScroll: () => void): () => void {
  const anchor = useRef<ScrollAnchor | null>(null);
  const latest = useRef(commits);
  latest.current = commits;
  const remember = useCallback(() => {
    anchor.current = anchorAt(scrollRef.current?.scrollTop ?? 0, rowHeight, (i) => latest.current[i]?.id);
    onScroll();
  }, [scrollRef, onScroll]);

  useLayoutEffect(() => {
    const element = scrollRef.current;
    if (!element) return;
    const indexById = new Map(latest.current.map((c, i) => [c.id, i]));
    const top = anchoredScrollTop(anchor.current, (id) => indexById.get(id) ?? -1, rowHeight);
    if (top !== null) element.scrollTop = top;
    remember();
  }, [version, scrollRef, remember]);

  return remember;
}

/**
 * Scrolls the selection into view when it moves to another commit, however it moved. Commits added above it move
 * its row, not the selection, so they leave the view where it is.
 */
function useSelectionInView(scrollRef: RefObject<HTMLElement | null>, selected: { index: number; id: string | undefined }, height: number, onScroll: () => void) {
  const index = useRef(selected.index);
  index.current = selected.index;
  const selectedId = selected.id;
  useEffect(() => {
    const element = scrollRef.current;
    if (!element || index.current < 0) return;
    element.scrollTop = scrollToShow(index.current, element.scrollTop, element.clientHeight || height, rowHeight);
    // The scroll event comes a frame later; the rows around the selection are needed now.
    onScroll();
  }, [scrollRef, selectedId, height, onScroll]);
}

interface RowProps {
  commit: CommitEntry;
  index: number;
  count: number;
  color: string;
  markers: Marker[] | undefined;
  faded: boolean;
  selected: boolean;
  indent: number;
  onSelect(index: number): void;
  now: Date;
}

function CommitRow({ commit: c, index, count, color, markers = [], faded, selected, indent, onSelect, now }: RowProps) {
  return (
    <div
      id={optionId(index)}
      data-index={index}
      role="option"
      aria-selected={selected}
      aria-setsize={count}
      aria-posinset={index + 1}
      className={`commit-row${selected ? " selected" : ""}${faded ? " faded" : ""}`}
      style={{ top: index * rowHeight, paddingLeft: indent }}
      onClick={() => onSelect(index)}
    >
      {markers.map((m) => (
        <MarkerChip key={`${m.kind}:${m.name}`} marker={m} color={color} />
      ))}
      <span className="subject">{c.subject || "(no subject)"}</span>
      <span className="author">{c.authorName}</span>
      <time className="age" dateTime={c.authoredAt}>
        {formatAge(c.authoredAt, now)}
      </time>
    </div>
  );
}

/**
 * Where a row's menu opens: at the pointer, or under the row's start for the menu key and Shift+F10, which raise
 * contextmenu with no pointer position.
 */
function menuPoint(event: MouseEvent, row: HTMLElement | null): { x: number; y: number } {
  if (event.clientX !== 0 || event.clientY !== 0 || row === null) return { x: event.clientX, y: event.clientY };
  const rect = row.getBoundingClientRect();
  return { x: rect.left + 24, y: rect.bottom };
}

function optionId(index: number): string {
  return `commit-row-${index}`;
}
