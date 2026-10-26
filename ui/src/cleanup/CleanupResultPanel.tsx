import type { StepResult } from "../protocol";
import { summarize } from "./outcome";
import type { Shown } from "./useCleanup";

interface Props {
  shown: Shown;
  /** The log still has something of this cleanup to take back. False after an undo, which cannot be undone. */
  canUndo: boolean;
  busy: boolean;
  onUndo(): void;
  onDismiss(): void;
}

/** What the last cleanup removed, what it kept as a backup, and what it skipped and why. Offers "Undo". */
export function CleanupResultPanel({ shown, canUndo, busy, onUndo, onDismiss }: Props) {
  const { done, backups, notDone } = summarize(shown.result, shown.undo);
  return (
    <section className="cleanup-result" aria-label={shown.undo ? "Undo result" : "Cleanup result"}>
      <h2>{shown.undo ? "Undone" : "Cleaned up"}</h2>
      <StepList title={shown.undo ? "Put back" : "Removed"} steps={done} />
      <StepList title="Kept as a backup" steps={backups} />
      <StepList title="Not done" steps={notDone} />
      <div className="item-actions">
        {canUndo && (
          <button className="abtn" disabled={busy} onClick={onUndo}>
            Undo
          </button>
        )}
        <button className="abtn" onClick={onDismiss}>
          Close
        </button>
      </div>
    </section>
  );
}

function StepList({ title, steps }: { title: string; steps: StepResult[] }) {
  if (steps.length === 0) return null;
  return (
    <div className="result-group">
      <h3>{title}</h3>
      <ul aria-label={title}>
        {steps.map((s) => (
          <li key={`${s.leftover}/${s.step}`}>
            {s.description}
            {s.reason && <span className="why">{s.reason}</span>}
          </li>
        ))}
      </ul>
    </div>
  );
}
