import { useEffect, useState } from "react";
import type { HistoryFile, Hunk, WorktreeChanges } from "../protocol";
import { messageOf } from "../model/format";
import { isCancelled, type RpcClient } from "../rpc";
import type { Entry } from "./entries";

export type FileState =
  | { kind: "loading" }
  /** A side is null when the file does not exist there: new or deleted. */
  | { kind: "ready"; original: string | null; modified: string | null; hunks: Hunk[] }
  /** Nothing to compare as text, with why: binary, too large, a submodule or a folder. */
  | { kind: "noText"; message: string }
  | { kind: "failed"; message: string };

/**
 * The diff of one row, read again whenever the status is, so the hunks' ids always match the file as it is now. The
 * text on screen stays while the next read runs, so staging a hunk does not blank the diff.
 */
export function useChangesFile(client: RpcClient, repoId: string, status: WorktreeChanges, entry: Entry): FileState {
  const [state, setState] = useState<FileState>({ kind: "loading" });
  const { file, side } = entry;

  useEffect(() => {
    const plain = withoutText(entry);
    if (plain) {
      setState({ kind: "noText", message: plain });
      return;
    }
    const abort = new AbortController();
    const params = { repoId, worktree: status.worktree, path: file.path, oldPath: file.oldPath, staged: side === "staged", headId: file.headId, indexId: file.indexId };
    client
      .call("changes.file", params, { signal: abort.signal })
      .then((answer) => setState(stateOf(answer.old, answer.new, answer.hunks)))
      .catch((error: unknown) => {
        if (!isCancelled(error)) setState({ kind: "failed", message: messageOf(error) });
      });
    return () => abort.abort();
    // The status is a dependency on purpose: a new one means the file may have changed.
  }, [client, repoId, status, file, side, entry]);

  return state;
}

function withoutText({ file }: Entry): string | null {
  if (file.kind === "Submodule") return "This is a submodule. Its commits live in another repo, so there is no text to compare.";
  if (file.kind === "NestedRepo") return "This folder holds a repo of its own, so there is no text to compare.";
  if (file.kind === "Conflicted") return "This file has a merge conflict. Resolving conflicts comes in a later version of Diffract.";
  return null;
}

function stateOf(original: HistoryFile | null, modified: HistoryFile | null, hunks: Hunk[]): FileState {
  const plain = [original, modified].find((side) => side !== null && side.kind !== "Text");
  if (plain) return { kind: "noText", message: plain.message ?? "There is no text to compare." };
  return { kind: "ready", original: textOf(original), modified: textOf(modified), hunks };
}

function textOf(side: HistoryFile | null): string | null {
  return side === null ? null : (side.text ?? "");
}
