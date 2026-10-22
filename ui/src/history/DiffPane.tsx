import { useMemo } from "react";
import type { FileChange } from "../protocol";
import type { RpcClient } from "../rpc";
import { MonacoDiff } from "./MonacoDiff";
import { useFileSides } from "./useFileSides";

interface Props {
  client: RpcClient;
  repoId: string;
  file: FileChange;
  onClose(): void;
}

/** One changed file of the selected commit, against its first parent. */
export function DiffPane({ client, repoId, file, onClose }: Props) {
  return (
    <section className="diff-pane" aria-label={`Diff of ${file.path}`}>
      <div className="diff-bar">
        <span className="diff-path" title={file.path}>
          {file.oldPath ? `${file.oldPath} → ${file.path}` : file.path}
        </span>
        <span className="grow" />
        <button className="abtn small" onClick={onClose} title="Close the diff (Esc)">
          Close
        </button>
      </div>
      <DiffBody client={client} repoId={repoId} file={file} />
    </section>
  );
}

function DiffBody({ client, repoId, file }: Omit<Props, "onClose">) {
  const state = useFileSides(client, repoId, file);
  const sides = useMemo(() => (state.kind === "ready" ? { path: file.path, original: state.original, modified: state.modified } : null), [state, file.path]);

  if (sides) return <MonacoDiff sides={sides} />;
  switch (state.kind) {
    case "loading":
      return <p className="empty">Loading…</p>;
    case "failed":
      return (
        <p className="empty" role="alert">
          Could not read this file: {state.message}
        </p>
      );
    default:
      return <p className="empty">{state.kind === "noText" ? state.message : ""}</p>;
  }
}
