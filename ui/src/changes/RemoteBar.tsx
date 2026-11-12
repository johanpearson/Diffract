import { IconButton } from "../components/IconButton";
import { FetchIcon, PruneIcon, PullIcon, PushIcon } from "../components/icons";
import type { WorktreeChanges } from "../protocol";
import { pullAction, pushAction, type RemoteAction, type RemoteKind } from "./remoteActions";
import type { Remote } from "./useRemote";

interface Props {
  status: WorktreeChanges;
  remote: Remote;
  /** Something else is running, or the worktree may be an agent's. */
  disabled: boolean;
  /** Prunes the remote this branch follows, or origin. */
  onPrune(remote: string): void;
}

const icons: Record<RemoteKind, () => React.JSX.Element> = { fetch: FetchIcon, pull: PullIcon, push: PushIcon };

/** Fetch, Pull and Push in the bar, as icons. What runs, and what came of it, shows in the command window. */
export function RemoteActions({ status, remote, disabled, onPrune }: Props) {
  const off = disabled || remote.running !== null;
  return (
    <div className="remote-actions" role="group" aria-label="Remote">
      <RemoteButton kind="fetch" action={{ label: "Fetch", reason: null }} remote={remote} disabled={off} />
      <IconButton
        label="Prune"
        title={`Prune: remove the copies of branches that ${remoteOf(status)} no longer has`}
        icon={<PruneIcon />}
        disabled={off}
        onClick={() => onPrune(remoteOf(status))}
      />
      <RemoteButton kind="pull" action={pullAction(status)} remote={remote} disabled={off} />
      <RemoteButton kind="push" action={pushAction(status)} remote={remote} disabled={off} />
    </div>
  );
}

function RemoteButton({ kind, action, remote, disabled }: { kind: RemoteKind; action: RemoteAction; remote: Remote; disabled: boolean }) {
  const Glyph = icons[kind];
  return (
    <IconButton
      label={action.label}
      title={action.reason ?? undefined}
      icon={<Glyph />}
      disabled={disabled || action.reason !== null}
      onClick={() => remote.start(kind, action.label)}
    />
  );
}

/** The remote the branch follows, such as "origin" for "origin/main", or origin when it follows none. */
export function remoteOf({ upstream }: WorktreeChanges): string {
  const slash = upstream?.indexOf("/") ?? -1;
  return upstream !== null && slash > 0 ? upstream.slice(0, slash) : "origin";
}
