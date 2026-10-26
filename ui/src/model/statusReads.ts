import { useCallback, useEffect, useRef, useState } from "react";
import type { WorktreeChanges } from "../protocol";
import type { RpcClient } from "../rpc";
import { concerns } from "./worktrees";

const asked = new WeakMap<RpcClient, Map<string, Promise<WorktreeChanges>>>();

/**
 * A worktree's status, with one git call for everyone who asks at the same moment, such as the Changes view and the
 * branch bar when the same change reaches both. Only calls made in the same turn share an answer: one asked for
 * later, after something may have changed, gets a call of its own. Starting git is slow on a busy machine.
 */
export function readStatus(client: RpcClient, repoId: string, worktree: string): Promise<WorktreeChanges> {
  const calls = asked.get(client) ?? new Map<string, Promise<WorktreeChanges>>();
  asked.set(client, calls);
  const key = `${repoId}\n${worktree.toLowerCase()}`;
  const running = calls.get(key);
  if (running) return running;
  const call = client.call("changes.status", { repoId, worktree });
  calls.set(key, call);
  queueMicrotask(() => calls.delete(key));
  return call;
}

/**
 * One worktree's branch, upstream, and ahead and behind, read when shown and again when it changes. Null until the
 * first answer, and while git cannot be read: what is shown is never a guess.
 */
export function useWorktreeStatus(client: RpcClient, repoId: string, worktree: string) {
  const [status, setStatus] = useState<WorktreeChanges | null>(null);
  // Only the newest answer counts, as in the Changes view.
  const latest = useRef(0);

  const read = useCallback(() => {
    const seq = ++latest.current;
    readStatus(client, repoId, worktree).then(
      (next) => seq === latest.current && setStatus(next),
      () => seq === latest.current && setStatus(null),
    );
  }, [client, repoId, worktree]);

  useEffect(() => {
    setStatus(null);
    if (worktree) read();
  }, [read, worktree]);
  useEffect(() => client.onNotification("repo.changed", ({ changes }) => concerns(changes, worktree) && read()), [client, worktree, read]);

  /** A status that came with another answer, such as a pull's. */
  const apply = useCallback((next: WorktreeChanges) => {
    ++latest.current;
    setStatus(next);
  }, []);
  return { status, apply };
}
