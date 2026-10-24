import { useCallback, useEffect, useRef, useState } from "react";
import { messageOf } from "../model/format";
import type { WorktreeChanges } from "../protocol";
import { isCancelled, type RpcClient } from "../rpc";
import { toneOf, type RemoteKind, type RemoteTone } from "./remoteActions";

export interface RemoteNote {
  text: string;
  tone: RemoteTone;
}

export interface Remote {
  /** What is running now, if anything. One at a time. */
  running: RemoteKind | null;
  /** What came of the last one. Null while one runs, after a cancel, and once dismissed. */
  note: RemoteNote | null;
  start(kind: RemoteKind): void;
  cancel(): void;
  dismiss(): void;
}

/**
 * Fetch, pull and push for one worktree. They run in the background, and cancelling stops git. Each answers with
 * the new status, which goes to `onStatus`. Leaving the worktree cancels what is running.
 */
export function useRemote(client: RpcClient, repoId: string, worktree: string, onStatus: (status: WorktreeChanges) => void): Remote {
  const [running, setRunning] = useState<RemoteKind | null>(null);
  const [note, setNote] = useState<RemoteNote | null>(null);
  const controller = useRef<AbortController | null>(null);
  useEffect(() => () => controller.current?.abort(), []);

  const start = useCallback(
    async (kind: RemoteKind) => {
      const mine = new AbortController();
      controller.current = mine;
      setRunning(kind);
      setNote(null);
      try {
        const result = await client.call(`remote.${kind}`, { repoId, worktree }, { signal: mine.signal });
        onStatus(result.status);
        setNote({ text: result.message, tone: toneOf(result.outcome) });
      } catch (error) {
        setNote(isCancelled(error) ? null : { text: messageOf(error), tone: "error" });
      } finally {
        controller.current = null;
        setRunning(null);
      }
    },
    [client, repoId, worktree, onStatus],
  );

  return {
    running,
    note,
    start: (kind) => void start(kind),
    cancel: () => controller.current?.abort(),
    dismiss: () => setNote(null),
  };
}
