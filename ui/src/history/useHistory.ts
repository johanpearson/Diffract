import { useEffect, useRef, useState } from "react";
import type { HistoryShow } from "../protocol";
import type { RpcClient } from "../rpc";
import { emptyHistory, HistorySync, type History, type HistoryTarget } from "./historySync";

export { emptyHistory, type History } from "./historySync";

/**
 * Reads the repo's whole history once per repo, page by page, and its refs. After that, whenever the repo's snapshot
 * or the toggles change, only what changed is read. Stops when the repo changes.
 */
export function useHistory(client: RpcClient, repoId: string, snapshotAt: string, show: HistoryShow): History {
  const [history, setHistory] = useState<History>(emptyHistory);
  const sync = useRef<HistorySync | null>(null);
  const target: HistoryTarget = { snapshotAt, show };
  const latest = useRef(target);
  latest.current = target;

  useEffect(() => {
    setHistory(emptyHistory);
    const current = new HistorySync(client, repoId, latest.current, setHistory);
    sync.current = current;
    void current.start();
    return () => current.stop();
  }, [client, repoId]);

  const { checkpoints, stashes } = show;
  useEffect(() => {
    sync.current?.want({ snapshotAt, show: { checkpoints, stashes } });
  }, [snapshotAt, checkpoints, stashes]);

  return history;
}
