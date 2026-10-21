import { useEffect, useState } from "react";
import type { GitUpdateResult, GitUpdateStage } from "../protocol";
import { GitUsers } from "./GitUpdateDialog";

/** Where an update is, after the user confirmed it. */
export type GitUpdateRun =
  | { kind: "running"; version: string; stage: GitUpdateStage; startedAt: number }
  | { kind: "done"; version: string; result: GitUpdateResult };

interface Props {
  run: GitUpdateRun;
  /** For how long it has run. Tests pass a fixed one. */
  clock: () => Date;
  onTryAgain(): void;
  onDownloadPage(): void;
  onDismiss(): void;
}

const stageText = {
  Downloading: "Downloading",
  Installing: "Installing",
} as const;

/**
 * Shows a running update and its result, in the bar under the top bar, so the app stays usable meanwhile.
 * winget gives no percentage, so the bar says which stage it is in and for how long.
 */
export function GitUpdateBar({ run, clock, onTryAgain, onDownloadPage, onDismiss }: Props) {
  const [now, setNow] = useState(() => clock().getTime());
  const running = run.kind === "running";
  useEffect(() => {
    if (!running) return;
    const timer = setInterval(() => setNow(clock().getTime()), 1000);
    return () => clearInterval(timer);
  }, [running, clock]);

  if (run.kind === "running") {
    return (
      <div className="notice quiet" role="status" aria-busy="true">
        <span className="spinner" aria-hidden="true" />
        <span>
          {stageText[run.stage]} Git for Windows {run.version}… {elapsed(now - run.startedAt)}
          {run.stage === "Installing" && <span className="hint-inline"> Windows may ask for permission.</span>}
        </span>
      </div>
    );
  }

  const { outcome, message, inUse } = run.result;
  return (
    <div className={outcome === "Failed" ? "notice" : "notice quiet"} role="status">
      <span>
        {message}
        {inUse.length > 0 && <GitUsers inUse={inUse} />}
      </span>
      {outcome === "Failed" && (
        <>
          <button className="abtn" onClick={onTryAgain}>
            Try again…
          </button>
          <button className="abtn" onClick={onDownloadPage}>
            Open download page
          </button>
        </>
      )}
      <button className="abtn" onClick={onDismiss}>
        Dismiss
      </button>
    </div>
  );
}

/** "0:42" or "3:05". */
function elapsed(ms: number): string {
  const seconds = Math.max(0, Math.floor(ms / 1000));
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
}
