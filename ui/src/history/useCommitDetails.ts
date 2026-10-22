import { useEffect, useState } from "react";
import type { CommitDetails } from "../protocol";
import { messageOf } from "../model/format";
import { isCancelled, type RpcClient } from "../rpc";

export type DetailsState =
  | { kind: "none" }
  | { kind: "loading" }
  | { kind: "ready"; details: CommitDetails }
  | { kind: "failed"; message: string };

/**
 * How long the selection must rest on a commit before its details are asked for. Holding an arrow key
 * then starts no git process for the commits passed on the way.
 */
export const DetailsDelayMs = 120;

/** The selected commit's details, asked for once the selection rests on it. */
export function useCommitDetails(client: RpcClient, repoId: string, commitId: string | null): DetailsState {
  const [state, setState] = useState<DetailsState>({ kind: "none" });

  useEffect(() => {
    if (commitId === null) {
      setState({ kind: "none" });
      return;
    }
    setState({ kind: "loading" });
    const abort = new AbortController();
    const timer = setTimeout(() => {
      client
        .call("history.details", { repoId, commitId }, { signal: abort.signal })
        .then((details) => setState({ kind: "ready", details }))
        .catch((error: unknown) => {
          if (!isCancelled(error)) setState({ kind: "failed", message: messageOf(error) });
        });
    }, DetailsDelayMs);
    return () => {
      clearTimeout(timer);
      abort.abort();
    };
  }, [client, repoId, commitId]);

  return state;
}
