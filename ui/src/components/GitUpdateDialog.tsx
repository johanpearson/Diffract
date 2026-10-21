import { useEffect, useRef, type KeyboardEvent } from "react";
import type { GitInUse } from "../protocol";

interface Props {
  /** The git Diffract uses now. */
  installed: string | null;
  /** The version offered, and the only one the sidecar will install. */
  version: string;
  /** The programs using git right now. Null while Diffract is still looking. */
  inUse: GitInUse[] | null;
  onCheckAgain(): void;
  onConfirm(): void;
  onCancel(): void;
}

/**
 * Asks before Git for Windows is updated. Nothing is installed until "Update" is pressed. The update itself then
 * runs in the background and shows in the update bar, so the dialog never locks the app while the installer runs.
 */
export function GitUpdateDialog({ installed, version, inUse, onCheckAgain, onConfirm, onCancel }: Props) {
  const cancel = useRef<HTMLButtonElement>(null);
  useEffect(() => cancel.current?.focus(), []);

  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === "Escape") onCancel();
  };

  return (
    <div className="overlay">
      <div className="dialog" role="dialog" aria-modal="true" aria-labelledby="git-update-title" onKeyDown={onKeyDown}>
        <h1 id="git-update-title">Update Git for Windows?</h1>
        <p className="versions-change">
          From <strong>{installed ?? "the installed version"}</strong> to <strong>{version}</strong>.
        </p>
        <ul>
          <li>winget downloads the installer and runs it. Windows asks for permission, because Git for Windows is installed for all users.</li>
          <li>Updating accepts the Git for Windows license (GPL-2.0).</li>
          <li>You can keep using Diffract meanwhile. It pauses its own git calls until the update is done.</li>
        </ul>
        <InUse inUse={inUse} onCheckAgain={onCheckAgain} />
        <div className="panel-actions">
          {/* Cancel first, so Enter on a dialog that just opened never starts an install. */}
          <button ref={cancel} className="abtn" onClick={onCancel}>
            Cancel
          </button>
          <button className="abtn primary" onClick={onConfirm}>
            Update
          </button>
        </div>
      </div>
    </div>
  );
}

function InUse({ inUse, onCheckAgain }: { inUse: GitInUse[] | null; onCheckAgain(): void }) {
  if (inUse === null) return <p className="in-use">Checking which programs use git…</p>;
  if (inUse.length === 0) return <p className="in-use">No other program is using git right now.</p>;
  return (
    <div className="in-use warn" role="alert">
      <p>The update will stop unless you close these programs first, because they are using git:</p>
      <GitUsers inUse={inUse} />
      <button className="abtn small" onClick={onCheckAgain}>
        Check again
      </button>
    </div>
  );
}

/** The programs using git, as a list. */
export function GitUsers({ inUse }: { inUse: GitInUse[] }) {
  return (
    <ul aria-label="Programs using git">
      {inUse.map((u) => (
        <li key={`${u.program}|${u.startedBy ?? ""}`}>
          {u.program}
          {u.startedBy && <span className="started-by"> in {u.startedBy}</span>}
        </li>
      ))}
    </ul>
  );
}
