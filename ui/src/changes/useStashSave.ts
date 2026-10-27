import { useCallback } from "react";
import { useCommands } from "../commands/Commands";
import type { WorktreeChanges } from "../protocol";
import type { RpcClient } from "../rpc";

interface Where {
  client: RpcClient;
  repoId: string;
  worktree: string;
}

interface Lists {
  apply(status: WorktreeChanges): void;
  read(): void;
  setBusy(busy: boolean): void;
}

/**
 * "Stash changes" in the command window, which says why when git did not stash, such as when there is nothing to.
 * The message is the commit box's; the sidecar uses its first line, or lets git name the stash when it is empty.
 * Resolves with whether it stashed.
 */
export function useStashSave({ client, repoId, worktree }: Where, { apply, read, setBusy }: Lists) {
  const commands = useCommands();
  return useCallback(
    async (message: string, includeUntracked: boolean) => {
      setBusy(true);
      try {
        const result = await commands.run({
          kind: "stash",
          title: "Stash changes",
          call: (operation) => client.call("stash.push", { repoId, worktree, message, includeUntracked, operation }),
          outcome: (r) => ({ message: r.message, tone: "done" }),
        });
        if (result) apply(result.status);
        else read();
        return result !== null;
      } finally {
        setBusy(false);
      }
    },
    [commands, client, repoId, worktree, apply, read, setBusy],
  );
}
