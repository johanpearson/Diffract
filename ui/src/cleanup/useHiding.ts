import { useCallback } from "react";
import { messageOf } from "../model/format";
import type { Leftover, RepoState } from "../protocol";
import type { RpcClient } from "../rpc";
import { toSelected } from "./selection";

export interface Hiding {
  /** Leaves the row out of the list and its counts until it changes. Changes nothing in the repo. */
  hide(leftover: Leftover): void;
  /** Puts a hidden row back in the list. */
  unhide(leftover: Leftover): void;
}

/** Where hiding reports to: the repo the rows are of, and what to do with the state the sidecar answers with. */
export interface HidingTarget {
  repoId: string;
  onState(state: RepoState): void;
  showNotice(message: string): void;
}

/** Hiding and showing rows again. The sidecar remembers them in Diffract's own folder, never in the repo. */
export function useHiding(client: RpcClient, { repoId, onState, showNotice }: HidingTarget): Hiding {
  const change = useCallback(
    (method: "cleanup.hide" | "cleanup.unhide", leftover: Leftover) => {
      client.call(method, { repoId, leftover: toSelected(leftover) }).then(onState, (error: unknown) => showNotice(messageOf(error)));
    },
    [client, repoId, onState, showNotice],
  );
  return {
    hide: (leftover) => change("cleanup.hide", leftover),
    unhide: (leftover) => change("cleanup.unhide", leftover),
  };
}
