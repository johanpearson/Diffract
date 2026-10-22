import { useMemo, useState } from "react";
import type { AppHost } from "../host";
import type { FileChange } from "../protocol";
import type { RpcClient } from "../rpc";
import { MonacoDiff } from "./MonacoDiff";
import type { DiffLayout, FileView } from "./monacoEditor";
import { useFileSides } from "./useFileSides";

/** The storage key for the diff layout the user chose last. */
export const DiffLayoutKey = "diffract.diffLayout";

interface Props {
  client: RpcClient;
  repoId: string;
  file: FileChange;
  storage: AppHost["storage"];
  onClose(): void;
}

/** One changed file of the selected commit, against its first parent. An added or deleted file shows on its own. */
export function DiffPane({ client, repoId, file, storage, onClose }: Props) {
  const [layout, setLayout] = useStoredLayout(storage);
  const [view, setView] = useState<FileView | null>(null);
  // Only a file that exists on both sides has changes to move between and a layout to pick.
  const twoSides = file.oldId !== null && file.newId !== null && !file.isBinary && !file.isSubmodule;

  return (
    <section className="diff-pane" aria-label={`Diff of ${file.path}`}>
      <div className="diff-bar">
        <span className="diff-path" title={file.path}>
          {file.oldPath ? `${file.oldPath} → ${file.path}` : file.path}
        </span>
        <span className="grow" />
        {twoSides && <DiffTools layout={layout} onLayout={setLayout} view={view} />}
        <button className="abtn small" onClick={onClose} title="Close the diff (Esc)">
          Close
        </button>
      </div>
      <DiffBody client={client} repoId={repoId} file={file} layout={layout} onView={setView} />
    </section>
  );
}

const layoutLabels: Record<DiffLayout, string> = { sideBySide: "Side by side", inline: "Inline" };

function DiffTools({ layout, onLayout, view }: { layout: DiffLayout; onLayout(layout: DiffLayout): void; view: FileView | null }) {
  return (
    <>
      <button className="abtn small" disabled={!view} onClick={() => view?.goToChange("previous")} title="Previous change" aria-label="Previous change">
        ↑ Previous
      </button>
      <button className="abtn small" disabled={!view} onClick={() => view?.goToChange("next")} title="Next change" aria-label="Next change">
        ↓ Next
      </button>
      <div className="segmented" role="group" aria-label="Diff layout">
        {(["sideBySide", "inline"] as const).map((l) => (
          <button key={l} className="abtn small" aria-pressed={layout === l} onClick={() => onLayout(l)}>
            {layoutLabels[l]}
          </button>
        ))}
      </div>
    </>
  );
}

function useStoredLayout(storage: AppHost["storage"]): [DiffLayout, (layout: DiffLayout) => void] {
  const [layout, setLayout] = useState<DiffLayout>(() => (storage.get(DiffLayoutKey) === "inline" ? "inline" : "sideBySide"));
  return [
    layout,
    (value) => {
      setLayout(value);
      storage.set(DiffLayoutKey, value);
    },
  ];
}

interface BodyProps {
  client: RpcClient;
  repoId: string;
  file: FileChange;
  layout: DiffLayout;
  onView(view: FileView | null): void;
}

function DiffBody({ client, repoId, file, layout, onView }: BodyProps) {
  const state = useFileSides(client, repoId, file);
  const sides = useMemo(() => (state.kind === "ready" ? { path: file.path, original: state.original, modified: state.modified } : null), [state, file.path]);

  if (sides) return <MonacoDiff sides={sides} layout={layout} onView={onView} />;
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
