import type { WorktreeChanges } from "../protocol";
import { pullAction, pushAction, type RemoteAction, type RemoteKind } from "./remoteActions";
import type { Remote } from "./useRemote";

interface Props {
  status: WorktreeChanges;
  remote: Remote;
  /** Something else is running, or the worktree may be an agent's. */
  disabled: boolean;
}

/** Fetch, Pull and Push in the bar. What runs, and what came of it, shows in the command window. */
export function RemoteActions({ status, remote, disabled }: Props) {
  const off = disabled || remote.running !== null;
  return (
    <div className="remote-actions" role="group" aria-label="Remote">
      <RemoteButton kind="fetch" action={{ label: "Fetch", reason: null }} remote={remote} disabled={off} />
      <RemoteButton kind="pull" action={pullAction(status)} remote={remote} disabled={off} />
      <RemoteButton kind="push" action={pushAction(status)} remote={remote} disabled={off} />
    </div>
  );
}

function RemoteButton({ kind, action, remote, disabled }: { kind: RemoteKind; action: RemoteAction; remote: Remote; disabled: boolean }) {
  return (
    <button className="abtn small" disabled={disabled || action.reason !== null} title={action.reason ?? undefined} onClick={() => remote.start(kind, action.label)}>
      {action.label}
    </button>
  );
}
