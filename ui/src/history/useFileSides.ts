import { useEffect, useState } from "react";
import type { FileChange, HistoryFile } from "../protocol";
import { messageOf } from "../model/format";
import { isCancelled, type RpcClient } from "../rpc";

export type SidesState =
  | { kind: "loading" }
  /** A side is null when the file does not exist there: added or deleted. */
  | { kind: "ready"; original: string | null; modified: string | null }
  /** Nothing to compare as text, with why: binary, too large or a submodule. */
  | { kind: "noText"; message: string }
  | { kind: "failed"; message: string };

/** The two sides of a changed file. An added file has no left side, a deleted one no right side. */
export function useFileSides(client: RpcClient, repoId: string, file: FileChange): SidesState {
  const [state, setState] = useState<SidesState>({ kind: "loading" });

  useEffect(() => {
    const plain = withoutText(file);
    if (plain) {
      setState({ kind: "noText", message: plain });
      return;
    }
    setState({ kind: "loading" });
    const abort = new AbortController();
    const side = (blobId: string | null) =>
      blobId === null ? Promise.resolve(null) : client.call("history.file", { repoId, blobId }, { signal: abort.signal });
    Promise.all([side(file.oldId), side(file.newId)])
      .then(([original, modified]) => setState(sidesOf(original, modified)))
      .catch((error: unknown) => {
        if (!isCancelled(error)) setState({ kind: "failed", message: messageOf(error) });
      });
    return () => abort.abort();
  }, [client, repoId, file]);

  return state;
}

/** Why a file has no text to compare, known before asking the sidecar, or null. */
function withoutText(file: FileChange): string | null {
  if (file.isSubmodule) return "This is a submodule. Its commits live in another repo, so there is no text to compare.";
  if (file.isBinary) return "This is a binary file, so there is no text to compare.";
  return null;
}

function sidesOf(original: HistoryFile | null, modified: HistoryFile | null): SidesState {
  const plain = [original, modified].find((side) => side !== null && side.kind !== "Text");
  if (plain) return { kind: "noText", message: plain.message ?? "There is no text to compare." };
  return { kind: "ready", original: textOf(original), modified: textOf(modified) };
}

function textOf(side: HistoryFile | null): string | null {
  return side === null ? null : (side.text ?? "");
}
