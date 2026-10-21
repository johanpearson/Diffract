import { useEffect, useRef, type KeyboardEvent } from "react";
import type { GitUpdateResult } from "../protocol";

/** Where the update is: waiting for the user's yes, running, or finished. `version` is the one offered and confirmed. */
export type GitUpdateStage =
  | { kind: "confirm"; version: string }
  | { kind: "running"; version: string }
  | { kind: "done"; version: string; result: GitUpdateResult };

interface Props {
  stage: GitUpdateStage;
  /** The git Diffract uses now. */
  installed: string | null;
  onConfirm(): void;
  onClose(): void;
  onDownloadPage(): void;
}

const titles = {
  Updated: "Git for Windows is updated",
  RestartNeeded: "Restart Windows to finish",
  Failed: "The update did not finish",
} as const;

/**
 * Confirms a Git for Windows update, then shows its progress and result. Nothing is installed until "Update" is
 * pressed, and the version named here is the only one the sidecar will install.
 */
export function GitUpdateDialog({ stage, installed, onConfirm, onClose, onDownloadPage }: Props) {
  const first = useRef<HTMLButtonElement>(null);
  useEffect(() => first.current?.focus(), [stage.kind]);

  // Escape closes the dialog, except while the update runs: it cannot be stopped halfway.
  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === "Escape" && stage.kind !== "running") onClose();
  };

  return (
    <div className="overlay">
      <div className="dialog" role="dialog" aria-modal="true" aria-labelledby="git-update-title" aria-busy={stage.kind === "running"} onKeyDown={onKeyDown}>
        {stage.kind === "confirm" && (
          <>
            <h1 id="git-update-title">Update Git for Windows?</h1>
            <p className="versions-change">
              From <strong>{installed ?? "the installed version"}</strong> to <strong>{stage.version}</strong>.
            </p>
            <ul>
              <li>winget downloads the installer and runs it. Windows asks for permission, because Git for Windows is installed for all users.</li>
              <li>Updating accepts the Git for Windows license (GPL-2.0).</li>
              <li>Close Git Bash, and stop agents that use git, before you start. Git cannot be updated while it is in use.</li>
            </ul>
            <div className="panel-actions">
              {/* Cancel first, so Enter on a dialog that just opened never starts an install. */}
              <button ref={first} className="abtn" onClick={onClose}>
                Cancel
              </button>
              <button className="abtn primary" onClick={onConfirm}>
                Update
              </button>
            </div>
          </>
        )}
        {stage.kind === "running" && (
          <>
            <h1 id="git-update-title">Updating Git for Windows to {stage.version}…</h1>
            <p role="status">This can take a few minutes. Diffract checks git again when it is done.</p>
          </>
        )}
        {stage.kind === "done" && (
          <>
            <h1 id="git-update-title">{titles[stage.result.outcome]}</h1>
            <p role="status">{stage.result.message}</p>
            <div className="panel-actions">
              {stage.result.outcome === "Failed" && (
                <button className="abtn" onClick={onDownloadPage}>
                  Open download page
                </button>
              )}
              <button ref={first} className="abtn primary" onClick={onClose}>
                Close
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
