import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { kindLabels } from "../model/format";
import type { CleanupPlanView, LeftoverPlanView, StepRef, StepView } from "../protocol";
import { leaveOutOf, plannedCount, stepKey } from "./outcome";

interface Props {
  plan: CleanupPlanView;
  onConfirm(leaveOut: StepRef[]): void;
  onCancel(): void;
}

/**
 * The confirm dialog: exactly what the cleanup will do, step by step, from the plan the sidecar keeps. Confirming
 * applies that plan and nothing else. A step with its own checkbox, such as deleting a worktree's branch, can be left
 * out. A step that cannot be undone says so before anything runs.
 */
export function CleanupDialog({ plan, onConfirm, onCancel }: Props) {
  const cancel = useRef<HTMLButtonElement>(null);
  useEffect(() => cancel.current?.focus(), []);
  const [unticked, setUnticked] = useState<ReadonlySet<string>>(new Set());
  const planned = plannedCount(plan);

  const toggle = (key: string) =>
    setUnticked((before) => {
      const next = new Set(before);
      if (!next.delete(key)) next.add(key);
      return next;
    });
  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === "Escape") onCancel();
  };

  return (
    <div className="overlay">
      <div className="dialog cleanup-dialog" role="dialog" aria-modal="true" aria-labelledby="cleanup-title" onKeyDown={onKeyDown}>
        <h1 id="cleanup-title">Clean up {planned === 1 ? "1 leftover" : `${planned} leftovers`}?</h1>
        <p>This is exactly what will happen, in this order. Nothing else is touched. A step whose leftover changed meanwhile is skipped, with why.</p>
        <ol className="cleanup-plan" aria-label="Planned steps">
          {plan.leftovers.map((leftover, l) => (
            <PlannedLeftover key={l} index={l} leftover={leftover} unticked={unticked} onToggle={toggle} />
          ))}
        </ol>
        <div className="panel-actions">
          {/* Cancel first and focused, so Enter on a dialog that just opened never removes anything. */}
          <button ref={cancel} className="abtn" onClick={onCancel}>
            Cancel
          </button>
          <button className="abtn primary" disabled={planned === 0} onClick={() => onConfirm(leaveOutOf(plan, unticked))}>
            Clean up
          </button>
        </div>
      </div>
    </div>
  );
}

interface LeftoverProps {
  index: number;
  leftover: LeftoverPlanView;
  unticked: ReadonlySet<string>;
  onToggle(key: string): void;
}

function PlannedLeftover({ index, leftover, unticked, onToggle }: LeftoverProps) {
  return (
    <li className="cleanup-leftover" aria-label={leftover.name}>
      <div>
        <span className="nm">{leftover.name}</span>
        <span className="kind">{kindLabels[leftover.kind]}</span>
      </div>
      {leftover.refused !== null ? (
        <p className="refused">Left as it is: {leftover.refused}</p>
      ) : (
        <ul className="cleanup-steps">
          {leftover.steps.map((step, s) => {
            const key = stepKey({ leftover: index, step: s });
            return <PlannedStep key={key} step={step} ticked={!unticked.has(key)} onToggle={() => onToggle(key)} />;
          })}
        </ul>
      )}
    </li>
  );
}

function PlannedStep({ step, ticked, onToggle }: { step: StepView; ticked: boolean; onToggle(): void }) {
  return (
    <li className={`cleanup-step ${step.kind === "Backup" ? "backup" : ""}`}>
      {step.optional ? (
        <label className="chk">
          <input type="checkbox" checked={ticked} onChange={onToggle} />
          {step.description}
        </label>
      ) : (
        <span>{step.description}</span>
      )}
      <UndoNote step={step} />
    </li>
  );
}

/** Nothing when undo is plain; otherwise its limit, or that it cannot be undone at all. */
function UndoNote({ step }: { step: StepView }) {
  if (!step.undoable) return <span className="undo-note final">Diffract cannot undo this.{step.undoNote ? ` ${step.undoNote}` : ""}</span>;
  return step.undoNote ? <span className="undo-note">{step.undoNote}</span> : null;
}
