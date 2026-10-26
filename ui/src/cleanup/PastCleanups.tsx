import { formatAge } from "../model/format";
import type { PastCleanup } from "../protocol";

interface Props {
  history: PastCleanup[];
  busy: boolean;
  onUndo(cleanupId: string): void;
  now: Date;
}

/** The operation log: past cleanups and undos, newest first, with "Undo" on each cleanup git can still take back. */
export function PastCleanups({ history, busy, onUndo, now }: Props) {
  if (history.length === 0) return null;
  return (
    <section className="past-cleanups" aria-labelledby="past-cleanups-title">
      <h2 id="past-cleanups-title">Past cleanups</h2>
      <ul aria-label="Past cleanups">
        {history.map((c) => (
          <li key={c.cleanupId} className="past-cleanup" aria-label={`${title(c)}, ${formatAge(c.at, now)}`}>
            <div>
              <span className="nm">{title(c)}</span>
              <span className="kind">{formatAge(c.at, now)}</span>
            </div>
            <div className="why">{c.leftovers.map((l) => l.name).join(", ")}</div>
            <div className="why">{counts(c)}</div>
            {c.canUndo && (
              <div className="item-actions">
                <button className="abtn" disabled={busy} onClick={() => onUndo(c.cleanupId)} aria-label={`Undo the cleanup of ${c.leftovers.map((l) => l.name).join(", ")}`}>
                  Undo
                </button>
              </div>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}

function title(c: PastCleanup): string {
  return c.undoes === null ? "Cleanup" : "Undo";
}

/** "3 steps done, 1 not done". */
function counts(c: PastCleanup): string {
  const done = c.steps.filter((s) => s.outcome === "Done").length;
  const notDone = c.steps.length - done;
  return notDone === 0 ? `${steps(done)} done` : `${steps(done)} done, ${notDone} not done`;
}

function steps(n: number): string {
  return n === 1 ? "1 step" : `${n} steps`;
}
