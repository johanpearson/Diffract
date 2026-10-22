import { useCallback, useEffect, useMemo, useRef, type KeyboardEvent } from "react";
import type { CommitEntry } from "../protocol";
import { formatAge } from "../model/format";
import { graphMetrics, graphWidth, type GraphRow } from "./drawGraph";
import { GraphCanvas } from "./GraphCanvas";
import type { Marker } from "./markers";
import { MarkerChip } from "./MarkerChip";
import { laneColor, useGraphColors } from "./useGraphColors";
import type { History } from "./useHistory";
import { useViewport } from "./useViewport";
import { moveSelection, scrollToShow, visibleRange } from "./viewport";

interface Props {
  history: History;
  markers: ReadonlyMap<string, Marker[]>;
  /** The selected row, or -1. */
  selected: number;
  onSelect(index: number): void;
  /** Enter: move to the details. */
  onOpen(): void;
  now: Date;
}

const { rowHeight } = graphMetrics;

/** Every commit, one row each, with the graph on the left. Only the rows in view are drawn. */
export function HistoryList({ history, markers, selected, onSelect, onOpen, now }: Props) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const { scrollTop, height, onScroll } = useViewport(scrollRef);
  const colors = useGraphColors(scrollRef);
  const { commits, rows } = history;
  const range = useMemo(() => visibleRange({ scrollTop, height }, rowHeight, commits.length), [scrollTop, height, commits.length]);
  const width = graphWidth(history.width);
  const row = useCallback((i: number): GraphRow => ({ layout: rows[i]!, isMerge: commits[i]!.parentIds.length > 1 }), [rows, commits]);

  // Keep the selection in view, however it moved.
  useEffect(() => {
    const element = scrollRef.current;
    if (!element || selected < 0) return;
    element.scrollTop = scrollToShow(selected, element.scrollTop, element.clientHeight || height, rowHeight);
    // The scroll event comes a frame later; the rows around the selection are needed now.
    onScroll();
  }, [selected, height, onScroll]);

  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === "Enter") return onOpen();
    const next = moveSelection(event.key, selected, Math.floor(height / rowHeight) - 1, commits.length);
    if (next === null) return;
    event.preventDefault();
    onSelect(next);
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
    >
      <div className="history-rows" role="presentation" style={{ height: commits.length * rowHeight }}>
        <GraphCanvas row={row} range={range} width={width} colors={colors} version={commits.length} />
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

interface RowProps {
  commit: CommitEntry;
  index: number;
  count: number;
  color: string;
  markers: Marker[] | undefined;
  selected: boolean;
  indent: number;
  onSelect(index: number): void;
  now: Date;
}

function CommitRow({ commit: c, index, count, color, markers = [], selected, indent, onSelect, now }: RowProps) {
  return (
    <div
      id={optionId(index)}
      role="option"
      aria-selected={selected}
      aria-setsize={count}
      aria-posinset={index + 1}
      className={`commit-row${selected ? " selected" : ""}`}
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

function optionId(index: number): string {
  return `commit-row-${index}`;
}
