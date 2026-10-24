import { useMemo, useState } from "react";
import type { AppHost } from "../host";
import { DiffTools, useStoredLayout } from "../history/DiffPane";
import { MonacoDiff } from "../history/MonacoDiff";
import type { FileView } from "../history/monacoEditor";
import type { Hunk, WorktreeChanges } from "../protocol";
import type { RpcClient } from "../rpc";
import { hunkActions, lineOf, verbOf } from "./hunkActions";
import type { Entry, Side } from "./entries";
import { useChangesFile, type FileState } from "./useChangesFile";

interface Props {
  client: RpcClient;
  repoId: string;
  status: WorktreeChanges;
  entry: Entry;
  storage: AppHost["storage"];
  onToggleHunk(hunk: Hunk): void;
  /** No hunk buttons while an action runs, or until an in-use worktree's warning is accepted. */
  locked: boolean;
}

/**
 * One row's diff: the index against the worktree for an unstaged row, HEAD against the index for a staged one. Each
 * change has its own Stage hunk or Unstage hunk button. A new or deleted file is one change, staged as a file.
 */
export function ChangesDiff({ client, repoId, status, entry, storage, onToggleHunk, locked }: Props) {
  const [layout, setLayout] = useStoredLayout(storage);
  const [view, setView] = useState<FileView | null>(null);
  const state = useChangesFile(client, repoId, status, entry);
  const shown = useShown(state, entry, locked, onToggleHunk);

  return (
    <section className="diff-pane" aria-label={`Diff of ${entry.file.path}`}>
      <div className="diff-bar">
        <DiffTitle entry={entry} />
        <span className="grow" />
        {shown.hunks.length > 0 && <HunkBar key={entry.key} hunks={shown.hunks} side={entry.side} view={view} onToggle={onToggleHunk} locked={locked} />}
        {shown.twoSides && <DiffTools layout={layout} onLayout={setLayout} view={view} moves={false} />}
      </div>
      {shown.sides ? <MonacoDiff sides={shown.sides} layout={layout} onView={setView} actions={shown.actions} /> : <Message state={state} />}
    </section>
  );
}

/** What the diff shows once the file is read: its sides, its changes, and a button above each. */
function useShown(state: FileState, entry: Entry, locked: boolean, onToggleHunk: (hunk: Hunk) => void) {
  const ready = state.kind === "ready" ? state : null;
  const path = entry.file.path;
  const sides = useMemo(() => (ready ? { path, original: ready.original, modified: ready.modified } : null), [ready, path]);
  const hunks = ready?.hunks ?? noHunks;
  const actions = useMemo(() => (locked ? [] : hunkActions(hunks, entry.side, onToggleHunk)), [hunks, locked, entry.side, onToggleHunk]);
  return { sides, hunks, actions, twoSides: sides?.original != null && sides.modified != null };
}

const noHunks: Hunk[] = [];

function DiffTitle({ entry: { file, side } }: { entry: Entry }) {
  return (
    <>
      <span className="diff-path" title={file.path}>
        {file.oldPath && side === "staged" ? `${file.oldPath} → ${file.path}` : file.path}
      </span>
      <span className="diff-side">{side === "staged" ? "Staged, against HEAD" : "Not staged, against the index"}</span>
    </>
  );
}

interface HunkBarProps {
  hunks: Hunk[];
  side: Side;
  view: FileView | null;
  onToggle(hunk: Hunk): void;
  locked: boolean;
}

/**
 * The changes of the file one at a time, for the keyboard and for screen readers, which cannot reach the buttons in
 * the diff itself. Moving to a change scrolls the diff to it.
 */
function HunkBar({ hunks, side, view, onToggle, locked }: HunkBarProps) {
  const [chosen, setChosen] = useState(0);
  const index = Math.min(chosen, hunks.length - 1);
  const hunk = hunks[index]!;
  const go = (next: number) => {
    setChosen(next);
    view?.revealLine(lineOf(hunks[next]!));
  };
  return (
    <div className="hunk-bar" role="group" aria-label="Changes in this file">
      <button className="abtn small" aria-label="Previous change" disabled={index === 0} onClick={() => go(index - 1)}>
        ↑
      </button>
      <span className="hunk-place" aria-live="polite">
        Change {index + 1} of {hunks.length}, line {lineOf(hunk)}
      </span>
      <button className="abtn small" aria-label="Next change" disabled={index === hunks.length - 1} onClick={() => go(index + 1)}>
        ↓
      </button>
      <button className="abtn small" disabled={locked} onClick={() => onToggle(hunk)}>
        {verbOf(side)} this change
      </button>
    </div>
  );
}

function Message({ state }: { state: FileState }) {
  switch (state.kind) {
    case "loading":
      return <p className="empty">Loading…</p>;
    case "failed":
      return (
        <p className="empty" role="alert">
          Could not read this file: {state.message}
        </p>
      );
    case "noText":
      return <p className="empty">{state.message}</p>;
    default:
      return null;
  }
}
