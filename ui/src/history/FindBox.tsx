import { forwardRef, useMemo, useState, type KeyboardEvent } from "react";
import type { CommitEntry } from "../protocol";
import { findMatches, nextMatch } from "./find";

interface Props {
  commits: readonly CommitEntry[];
  /** The selected row, or -1. */
  selected: number;
  onSelect(index: number): void;
}

/** Finds commits by hash, subject or author among those loaded. Enter goes to the next match, Shift+Enter back. */
export const FindBox = forwardRef<HTMLInputElement, Props>(function FindBox({ commits, selected, onSelect }, ref) {
  const [query, setQuery] = useState("");
  const matches = useMemo(() => findMatches(commits, query), [commits, query]);

  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === "Escape" && query !== "") {
      // Clearing the box is all Escape does here; it must not also close the diff.
      event.stopPropagation();
      return setQuery("");
    }
    if (event.key !== "Enter") return;
    event.preventDefault();
    const row = nextMatch(matches, selected, event.shiftKey);
    if (row !== null) onSelect(row);
  };

  return (
    <div className="find">
      <input
        ref={ref}
        type="search"
        aria-label="Find commits"
        placeholder="Find hash, subject or author"
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        onKeyDown={onKeyDown}
      />
      {query.trim() !== "" && (
        <span className="find-count" aria-live="polite">
          {countText(matches, selected)}
        </span>
      )}
    </div>
  );
});

function countText(matches: readonly number[], selected: number): string {
  if (matches.length === 0) return "No matches";
  const at = matches.indexOf(selected);
  return at >= 0 ? `${at + 1} of ${matches.length}` : `${matches.length} found`;
}
