import { useEffect, useRef, useState, type ReactNode } from "react";
import { formatAge, messageOf } from "../model/format";
import type { Row } from "../model/rows";
import type { ToggleName } from "../history/HistoryToggles";
import type { LeftoverContent } from "../protocol";
import { canShowInHistory, historyToggleFor, holdsCommits } from "./review";
import type { Cleanup } from "./useCleanup";

interface Props {
  row: Row;
  cleanup: Cleanup;
  onShowInHistory(commitId: string, toggle: ToggleName | null): void;
  now: Date;
  /** The row's other buttons, such as "Open folder", which share the one bar. */
  children?: ReactNode;
}

/**
 * What makes a row something to act on: what to check before removing it, a way to see it in History, and the
 * commits only it holds, read when the row is opened. All of the row's buttons sit in one bar.
 */
export function RowReview({ row, cleanup, onShowInHistory, now, children }: Props) {
  const l = row.leftover;
  const [open, setOpen] = useState(false);
  const buttons = [
    canShowInHistory(l) && (
      <button key="history" className="abtn" onClick={() => onShowInHistory(l.commit!, historyToggleFor(l.kind))} aria-label={`Show ${l.name} in History`}>
        Show in History
      </button>
    ),
    holdsCommits(l) && (
      <button key="commits" className="abtn" aria-expanded={open} onClick={() => setOpen((o) => !o)} aria-label={`${open ? "Hide" : "Show"} the commits of ${l.name}`}>
        {open ? "Hide commits" : "Show commits"}
      </button>
    ),
  ].filter(Boolean);
  return (
    <>
      {l.check && <div className="check">{l.check}</div>}
      {(buttons.length > 0 || children) && (
        <div className="item-actions">
          {buttons}
          {children}
        </div>
      )}
      {open && <RowCommits row={row} cleanup={cleanup} now={now} />}
    </>
  );
}

type Loaded = { kind: "loading" } | { kind: "loaded"; content: LeftoverContent } | { kind: "failed"; message: string };

/**
 * The commits only the row holds, newest first, read when it opens and again only when the row's commit moves. A
 * row is rebuilt on every refresh, so its key and commit say whether it changed, not the row itself.
 */
function RowCommits({ row, cleanup, now }: Omit<Props, "onShowInHistory">) {
  const [loaded, setLoaded] = useState<Loaded>({ kind: "loading" });
  const { inspect } = cleanup;
  const { key, leftover } = row;
  const latest = useRef(leftover);
  latest.current = leftover;
  useEffect(() => {
    let current = true;
    inspect(latest.current).then(
      (content) => current && setLoaded({ kind: "loaded", content }),
      (error: unknown) => current && setLoaded({ kind: "failed", message: messageOf(error) }),
    );
    return () => {
      current = false;
    };
  }, [inspect, key, leftover.commit]);

  if (loaded.kind === "loading") return <p className="row-commits empty">Reading its commits…</p>;
  if (loaded.kind === "failed") return <p className="row-commits empty" role="alert">Could not read its commits: {loaded.message}</p>;
  const { commits, more } = loaded.content;
  if (commits.length === 0) return <p className="row-commits empty">No commit exists only here.</p>;
  return (
    <ul className="row-commits" aria-label={`Commits of ${row.leftover.name}`}>
      {commits.map((c) => (
        <li key={c.id}>
          <span className="mono">{c.id.slice(0, 8)}</span> <span className="subject">{c.subject || "(no subject)"}</span>
          <span className="kind">
            {c.authorName}, {formatAge(c.committedAt, now)}
          </span>
        </li>
      ))}
      {more && <li className="more">Only the newest {commits.length} are shown.</li>}
    </ul>
  );
}
