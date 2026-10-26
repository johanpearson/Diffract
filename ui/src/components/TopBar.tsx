import type { ReactNode } from "react";
import type { AppInfo } from "../protocol";

interface Props {
  info: AppInfo | null;
  /** Opening a repo makes no sense yet, such as while starting or when git cannot be used. */
  canOpen: boolean;
  /** Git for Windows is being updated, which blocks opening a repo. */
  updating: boolean;
  onOpen(): Promise<void>;
  /** The open repo's branch bar, next to the name. */
  children?: ReactNode;
}

/** The app name, the open repo's branch with Fetch, Pull and Push, the git and sidecar versions, and "Open repo…". */
export function TopBar({ info, canOpen, updating, onOpen, children }: Props) {
  return (
    <header className="topbar">
      <span className="brand">Diffract</span>
      {children}
      <span className="grow" />
      {info && (
        <span className="versions">
          git {info.gitVersion ?? "not found"} · sidecar {info.sidecarVersion}
        </span>
      )}
      <button
        className="abtn"
        onClick={() => void onOpen()}
        disabled={!canOpen || updating}
        title={updating ? "Wait until Git for Windows is updated" : undefined}
      >
        Open repo…
      </button>
    </header>
  );
}
