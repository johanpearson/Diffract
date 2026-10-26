import { useCallback, useState } from "react";
import { useCommands } from "../commands/Commands";
import type { WorktreeChanges } from "../protocol";
import type { RpcClient } from "../rpc";
import { toneOf, type RemoteKind } from "./remoteActions";

export interface Remote {
  /** What is running now, if anything. One at a time. */
  running: RemoteKind | null;
  /** `title` is the button's text, such as "Push to origin/main", which the command window shows. */
  start(kind: RemoteKind, title: string): void;
}

/**
 * Fetch, pull and push for one worktree, in the command window, which can cancel them. Each answers with the new
 * status, which goes to `onStatus`.
 */
export function useRemote(client: RpcClient, repoId: string, worktree: string, onStatus: (status: WorktreeChanges) => void): Remote {
  const commands = useCommands();
  const [running, setRunning] = useState<RemoteKind | null>(null);

  const start = useCallback(
    async (kind: RemoteKind, title: string) => {
      setRunning(kind);
      try {
        const result = await commands.run({
          kind,
          title,
          cancellable: true,
          call: (operation, signal) => client.call(`remote.${kind}`, { repoId, worktree, operation }, { signal }),
          outcome: (r) => ({ message: r.message, tone: toneOf(r.outcome) === "warn" ? "warning" : "done" }),
        });
        if (result) onStatus(result.status);
      } finally {
        setRunning(null);
      }
    },
    [commands, client, repoId, worktree, onStatus],
  );

  return { running, start: (kind, title) => void start(kind, title) };
}
