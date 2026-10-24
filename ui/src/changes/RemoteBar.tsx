import type { WorktreeChanges } from "../protocol";
import { pullAction, pushAction, runningText, type RemoteAction, type RemoteKind } from "./remoteActions";
import type { Remote } from "./useRemote";

interface Props {
  status: WorktreeChanges;
  remote: Remote;
  /** Something else is running, or the worktree may be an agent's. */
  disabled: boolean;
}

/** Fetch, Pull and Push in the bar, or what is running with Cancel. A quiet result shows next to them. */
export function RemoteActions({ status, remote, disabled }: Props) {
  const quiet = remote.note?.tone === "ok" ? remote.note.text : "";
  return (
    <div className="remote-actions" role="group" aria-label="Remote">
      {remote.running ? (
        <Running kind={remote.running} onCancel={remote.cancel} />
      ) : (
        <>
          <RemoteButton kind="fetch" action={{ label: "Fetch", reason: null }} remote={remote} disabled={disabled} />
          <RemoteButton kind="pull" action={pullAction(status)} remote={remote} disabled={disabled} />
          <RemoteButton kind="push" action={pushAction(status)} remote={remote} disabled={disabled} />
        </>
      )}
      <span className="muted remote-note" aria-live="polite">
        {quiet}
      </span>
    </div>
  );
}

function RemoteButton({ kind, action, remote, disabled }: { kind: RemoteKind; action: RemoteAction; remote: Remote; disabled: boolean }) {
  return (
    <button className="abtn small" disabled={disabled || action.reason !== null} title={action.reason ?? undefined} onClick={() => remote.start(kind)}>
      {action.label}
    </button>
  );
}

function Running({ kind, onCancel }: { kind: RemoteKind; onCancel(): void }) {
  return (
    <>
      <span className="remote-running">{runningText[kind]}</span>
      <button className="abtn small" onClick={onCancel}>
        Cancel
      </button>
    </>
  );
}

/** A result that needs the user: a pull that could not fast-forward, a rejected push, or a failure with git's words. */
export function RemoteNotice({ remote }: { remote: Remote }) {
  const { note } = remote;
  if (note === null || note.tone === "ok") return null;
  return (
    <div className={`notice remote-notice ${note.tone}`} role="alert">
      <span>{note.text}</span>
      <button className="abtn" onClick={remote.dismiss}>
        Dismiss
      </button>
    </div>
  );
}
