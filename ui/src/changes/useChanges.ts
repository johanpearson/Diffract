import { useCallback, useEffect, useRef, useState } from "react";
import type { Hunk, RepoChange, WorktreeChanges } from "../protocol";
import { messageOf } from "../model/format";
import type { RpcClient } from "../rpc";
import { pathsOf, type Entry } from "./entries";

export interface Changes {
  /** Null until the first status arrives. */
  status: WorktreeChanges | null;
  /** Why the status could not be read, or why the last action failed. Null when all is well. */
  problem: string | null;
  dismissProblem(): void;
  /** An action is running. The lists stay as they are until it answers. */
  busy: boolean;
  stage(entries: readonly Entry[]): void;
  unstage(entries: readonly Entry[]): void;
  /** Stages a hunk of an unstaged row, or unstages one of a staged row. */
  toggleHunk(entry: Entry, hunk: Hunk): void;
  /** Resolves with null when it committed, or with why not, which the commit box shows. */
  commit(message: string, amend: boolean): Promise<string | null>;
  /** Shows a status that arrived with another answer, such as a pull's. */
  apply(status: WorktreeChanges): void;
}

/**
 * One worktree's changes, read when it is shown and again whenever the sidecar says it changed, and the actions that
 * change it. Each action answers with the new status, so the lists follow without waiting for the watcher.
 */
export function useChanges(client: RpcClient, repoId: string, worktree: string): Changes {
  const [status, setStatus] = useState<WorktreeChanges | null>(null);
  const problems = useProblems();
  const { setProblem, setReadProblem } = problems;
  const [busy, setBusy] = useState(false);
  // Only the newest answer counts: an older read that arrives late must not undo a newer one.
  const latest = useRef(0);

  const show = useCallback((seq: number, next: WorktreeChanges) => {
    if (seq !== latest.current) return;
    setStatus(next);
    // A status that arrived means git works, so a read that failed before no longer matters.
    setReadProblem(null);
  }, [setReadProblem]);

  const read = useCallback(() => {
    const seq = ++latest.current;
    client
      .call("changes.status", { repoId, worktree })
      .then((next) => show(seq, next))
      .catch((error: unknown) => seq === latest.current && setReadProblem(`Could not read the changes: ${messageOf(error)}`));
  }, [client, repoId, worktree, show, setReadProblem]);

  useEffect(() => {
    setStatus(null);
    read();
  }, [read]);
  useEffect(() => client.onNotification("repo.changed", ({ changes }) => concerns(changes, worktree) && read()), [client, worktree, read]);

  const run = useCallback(
    async (action: () => Promise<WorktreeChanges>): Promise<string | null> => {
      const seq = ++latest.current;
      setBusy(true);
      try {
        show(seq, await action());
        return null;
      } catch (error) {
        read();
        return messageOf(error);
      } finally {
        setBusy(false);
      }
    },
    [show, read],
  );

  const apply = useCallback((next: WorktreeChanges) => show(++latest.current, next), [show]);

  const act = useCallback((action: () => Promise<WorktreeChanges>) => void run(action).then((failed) => failed && setProblem(failed)), [run, setProblem]);

  return {
    status,
    problem: problems.problem,
    dismissProblem: problems.dismiss,
    busy,
    stage: (entries) => act(() => client.call("changes.stage", { repoId, worktree, paths: pathsOf(entries) })),
    unstage: (entries) => act(() => client.call("changes.unstage", { repoId, worktree, paths: pathsOf(entries) })),
    toggleHunk: (entry, hunk) => act(() => client.call(entry.side === "unstaged" ? "changes.stage" : "changes.unstage", { repoId, worktree, hunk: hunkOf(entry, hunk) })),
    commit: (message, amend) => run(async () => (await client.call("changes.commit", { repoId, worktree, message, amend })).status),
    apply,
  };
}

/**
 * Why the status could not be read, and why the last action failed. Kept apart, so a read that works again clears
 * its own problem, but never an action's that the user has not seen yet.
 */
function useProblems() {
  const [problem, setProblem] = useState<string | null>(null);
  const [readProblem, setReadProblem] = useState<string | null>(null);
  const dismiss = useCallback(() => {
    setProblem(null);
    setReadProblem(null);
  }, []);
  return { problem: problem ?? readProblem, setProblem, setReadProblem, dismiss };
}

function hunkOf(entry: Entry, hunk: Hunk) {
  return { path: entry.file.path, oldPath: entry.side === "staged" ? entry.file.oldPath : null, id: hunk.id };
}

/**
 * A change to this worktree's files or index, to refs (the upstream may have moved), or one that rebuilt everything.
 * Other worktrees' changes do not concern it.
 */
function concerns(changes: readonly RepoChange[], worktree: string): boolean {
  return changes.some((c) => c.path === null ? c.kind !== "WorktreeList" : samePath(c.path, worktree));
}

function samePath(a: string, b: string): boolean {
  const normalize = (p: string) => p.replaceAll("\\", "/").replace(/\/+$/, "").toLowerCase();
  return normalize(a) === normalize(b);
}
