import type { GitUpdateResult } from "../protocol";
import { GitUsers } from "./GitUpdateDialog";

interface Props {
  /** An update that failed, or that needs Windows to restart. A good update shows in the top bar's git version. */
  result: GitUpdateResult;
  /** Null when there is nothing to try again, such as after winget stopped offering the update. */
  onTryAgain: (() => void) | null;
  onDownloadPage(): void;
  onDismiss(): void;
}

/** What the user needs to do after an update: close the programs using git, or restart Windows. */
export function GitUpdateBar({ result, onTryAgain, onDownloadPage, onDismiss }: Props) {
  const failed = result.outcome === "Failed";
  return (
    <div className="notice" role="status">
      <div className="notice-text">
        {result.message}
        {result.inUse.length > 0 && <GitUsers inUse={result.inUse} />}
      </div>
      {failed && onTryAgain && (
        <button className="abtn" onClick={onTryAgain}>
          Try again…
        </button>
      )}
      {failed && (
        <button className="abtn" onClick={onDownloadPage}>
          Open download page
        </button>
      )}
      <button className="abtn" onClick={onDismiss}>
        Dismiss
      </button>
    </div>
  );
}
