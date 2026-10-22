import { useState } from "react";
import type { AppInfo } from "../protocol";
import type { GitUpdate } from "../model/useGitUpdate";
import { GitUpdateBar } from "./GitUpdateBar";
import { GitUpdateDialog } from "./GitUpdateDialog";

/** What the user needs to do after an update that did not simply work. Nothing otherwise. */
export function GitUpdateOutcome({ git, offered, onDownloadPage }: { git: GitUpdate; offered: string | null; onDownloadPage(): void }) {
  if (!git.result) return null;
  return (
    <GitUpdateBar
      result={git.result}
      onTryAgain={offered ? () => git.askToUpdate(offered) : null}
      onDownloadPage={onDownloadPage}
      onDismiss={git.dismissResult}
    />
  );
}

/**
 * A quiet offer of a newer Git for Windows, while git works and nothing else about the update is on screen.
 * "Not now" hides it until a newer version is offered.
 */
export function GitUpdateOffer({ git, info, offered }: { git: GitUpdate; info: AppInfo | null; offered: string | null }) {
  // The available version the user said "Not now" to. A newer one is offered again.
  const [declined, setDeclined] = useState<string | null>(null);
  const hidden = !offered || !info?.gitSupported || declined === offered || git.result !== null || git.running;
  if (hidden) return null;
  return (
    <div className="notice quiet" role="status">
      <span>
        Git for Windows {offered} is available. You have {git.check?.installedVersion}.
      </span>
      <button className="abtn" onClick={() => git.askToUpdate(offered)}>
        Update…
      </button>
      <button className="abtn" onClick={() => setDeclined(offered)}>
        Not now
      </button>
    </div>
  );
}

/** The confirm dialog for an update, while one is being confirmed. */
export function GitUpdateConfirmDialog({ git, info, onUpdated }: { git: GitUpdate; info: AppInfo | null; onUpdated(): Promise<void> }) {
  const confirm = git.confirm;
  if (!confirm) return null;
  return (
    <GitUpdateDialog
      installed={info?.gitVersion ?? null}
      version={confirm.version}
      inUse={confirm.inUse}
      onCheckAgain={git.findUsers}
      onConfirm={() => void git.update(confirm.version, () => void onUpdated())}
      onCancel={git.cancelConfirm}
    />
  );
}
