import { useEffect, useRef, type KeyboardEvent, type RefObject } from "react";
import { CommandAnimation } from "./CommandAnimation";
import type { CommandRun, OutputLine } from "./commandRun";

interface Props {
  run: CommandRun;
  onCancel(): void;
  onClose(): void;
}

const stateText: Record<CommandRun["state"], string> = { running: "Running", done: "Done", warning: "Nothing was changed", failed: "Failed" };

/**
 * A git command as it runs, like the one Git Extensions shows: an animation for what it does, the command and git's
 * output as it arrives, and then how it ended. Escape closes it once the command has ended.
 */
export function CommandWindow({ run, onCancel, onClose }: Props) {
  const box = useFocusWhileOpen();
  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key !== "Escape" || run.state === "running") return;
    event.stopPropagation();
    onClose();
  };

  return (
    <div className="overlay command-overlay">
      <div ref={box} className={`command-window ${run.state}`} role="dialog" aria-modal="true" aria-labelledby={`${run.id}-title`} tabIndex={-1} onKeyDown={onKeyDown}>
        <Head run={run} />
        <Output lines={run.lines} />
        <Message run={run} />
        <Actions run={run} onCancel={onCancel} onClose={onClose} />
      </div>
    </div>
  );
}

/** Focus moves into the window, and back to what had it when the window closes. */
function useFocusWhileOpen(): RefObject<HTMLDivElement | null> {
  const box = useRef<HTMLDivElement>(null);
  const opener = useRef<Element | null>(document.activeElement);
  useEffect(() => {
    box.current?.focus();
    const back = opener.current;
    return () => {
      if (back instanceof HTMLElement && back.isConnected) back.focus();
    };
  }, []);
  return box;
}

function Head({ run }: { run: CommandRun }) {
  return (
    <div className="command-head">
      <CommandAnimation kind={run.kind} state={run.state} />
      <div>
        <h2 id={`${run.id}-title`}>{run.title}</h2>
        <p className="command-state" role="status">
          {stateText[run.state]}
        </p>
      </div>
    </div>
  );
}

/** Git's output, kept scrolled to the newest line. */
function Output({ lines }: { lines: OutputLine[] }) {
  const log = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (log.current) log.current.scrollTop = log.current.scrollHeight;
  }, [lines]);
  return (
    <div ref={log} className="command-output" role="log" aria-label="Git output">
      {lines.map((line, index) => (
        <div key={index} className={`out-${line.kind.toLowerCase()}`}>
          {line.kind === "Command" ? `> ${line.text}` : line.text}
        </div>
      ))}
    </div>
  );
}

/** How it ended. A warning or a failure is read out at once. */
function Message({ run }: { run: CommandRun }) {
  if (!run.message) return null;
  const urgent = run.state === "failed" || run.state === "warning";
  return (
    <p className="command-message" role={urgent ? "alert" : undefined}>
      {run.message}
    </p>
  );
}

function Actions({ run, onCancel, onClose }: Props) {
  const running = run.state === "running";
  return (
    <div className="command-actions">
      {running && run.cancellable && (
        <button className="abtn" onClick={onCancel}>
          Cancel
        </button>
      )}
      {run.followUp && (
        <button className="abtn" onClick={run.followUp.run}>
          {run.followUp.label}
        </button>
      )}
      {!running && (
        <button className="abtn primary" onClick={onClose}>
          Close
        </button>
      )}
    </div>
  );
}
