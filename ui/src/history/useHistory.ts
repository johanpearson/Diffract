import { useEffect, useState } from "react";
import type { CommitEntry, HistoryRef } from "../protocol";
import { messageOf } from "../model/format";
import { isCancelled, type RpcClient } from "../rpc";
import { emptyLaneState, layoutLanes, type LaneRow, type LaneState } from "./lanes";

/** The history read so far, laid out in lanes as it arrives. */
export interface History {
  commits: CommitEntry[];
  /** One per commit, in the same order. */
  rows: LaneRow[];
  /** The widest the graph has been so far, in lanes. */
  width: number;
  refs: HistoryRef[];
  /** Every page has arrived. */
  done: boolean;
  /** Why reading failed, or null. */
  error: string | null;
}

export const emptyHistory: History = { commits: [], rows: [], width: 0, refs: [], done: false, error: null };

let reads = 0;

/** Reads the repo's whole history once per repo, page by page, and its refs. Stops reading when the repo changes. */
export function useHistory(client: RpcClient, repoId: string): History {
  const [history, setHistory] = useState<History>(emptyHistory);

  useEffect(() => {
    const token = `read-${++reads}`;
    const abort = new AbortController();
    let lanes: LaneState = emptyLaneState;
    setHistory(emptyHistory);

    const stop = client.onNotification("history.page", (page) => {
      if (page.token !== token) return;
      const { rows, state } = layoutLanes(page.commits, lanes);
      lanes = state;
      setHistory((h) => ({ ...h, commits: h.commits.concat(page.commits), rows: h.rows.concat(rows), width: state.width }));
    });
    const fail = (error: unknown) => {
      if (!abort.signal.aborted && !isCancelled(error)) setHistory((h) => ({ ...h, error: messageOf(error) }));
    };

    client
      .call("history.read", { repoId, token }, { signal: abort.signal })
      .then(() => setHistory((h) => ({ ...h, done: true })))
      .catch(fail);
    client
      .call("history.refs", { repoId }, { signal: abort.signal })
      .then((refs) => setHistory((h) => ({ ...h, refs })))
      .catch(fail);

    return () => {
      stop();
      abort.abort();
    };
  }, [client, repoId]);

  return history;
}
