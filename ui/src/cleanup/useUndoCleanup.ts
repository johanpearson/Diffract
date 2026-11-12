import { useCallback } from "react";
import { useCommands } from "../commands/Commands";
import type { RpcClient } from "../rpc";
import { needsAttention, outcomeLine, summarize } from "./outcome";

/** Undoes a cleanup from the operation log, in the command window under `title`, such as "Undo the prune". */
export function useUndoCleanup(client: RpcClient, repoId: string, title: string): (cleanupId: string) => void {
  const commands = useCommands();
  return useCallback(
    (cleanupId: string) =>
      void commands.run({
        kind: "cleanup",
        title,
        call: (operation) => client.call("cleanup.undo", { repoId, cleanupId, operation }),
        outcome: (r) => ({ message: outcomeLine(summarize(r, true), true), tone: needsAttention(r, []) ? "warning" : "done" }),
      }),
    [commands, client, repoId, title],
  );
}
