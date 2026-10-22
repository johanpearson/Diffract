import { useEffect, useState } from "react";
import type { FileChange, HistoryFile } from "../protocol";
import { messageOf } from "../model/format";
import { isCancelled, type RpcClient } from "../rpc";

export type SidesState =
  | { kind: "loading" }
  | { kind: "ready"; original: string; modified: string }
  /** Nothing to compare as text, with why: binary, too large or a submodule. */
  | { kind: "noText"; message: string }
  | { kind: "failed"; message: string };

/** The two sides of a changed file. An added file has an empty left side, a deleted one an empty right side. */
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
      blobId === null ? Promise.resolve(emptySide) : client.call("history.file", { repoId, blobId }, { signal: abort.signal });
    Promise.all([side(file.oldId), side(file.newId)])
      .then(([original, modified]) => setState(sidesOf(original, modified)))
      .catch((error: unknown) => {
        if (!isCancelled(error)) setState({ kind: "failed", message: messageOf(error) });
      });
    return () => abort.abort();
  }, [client, repoId, file]);

  return state;
}

const emptySide: HistoryFile = { kind: "Text", text: "", message: null };

/** Why a file has no text to compare, known before asking the sidecar, or null. */
function withoutText(file: FileChange): string | null {
  if (file.isSubmodule) return "This is a submodule. Its commits live in another repo, so there is no text to compare.";
  if (file.isBinary) return "This is a binary file, so there is no text to compare.";
  return null;
}

function sidesOf(original: HistoryFile, modified: HistoryFile): SidesState {
  const plain = [original, modified].find((side) => side.kind !== "Text");
  if (plain) return { kind: "noText", message: plain.message ?? "There is no text to compare." };
  return { kind: "ready", original: original.text ?? "", modified: modified.text ?? "" };
}
