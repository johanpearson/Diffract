import { useEffect, useMemo, useRef, useState } from "react";
import type { FileChange, RepoState } from "../protocol";
import type { RpcClient } from "../rpc";
import { CommitPanel } from "./CommitPanel";
import { DiffPane } from "./DiffPane";
import { FindBox } from "./FindBox";
import { HistoryList } from "./HistoryList";
import { markersByCommit } from "./markers";
import { useCommitDetails } from "./useCommitDetails";
import { useHistory, type History } from "./useHistory";

interface Props {
  client: RpcClient;
  state: RepoState;
  now: Date;
}

/** The repo's history as a graph, with the selected commit's details beside it and a file's diff below. */
export function HistoryView({ client, state, now }: Props) {
  const history = useHistory(client, state.repoId);
  const markers = useMemo(() => markersByCommit(history.refs, state.snapshot.worktrees), [history.refs, state.snapshot.worktrees]);
  const selection = useSelection(history);
  const details = useCommitDetails(client, state.repoId, selection.id);
  const diff = useOpenFile(selection.id);
  const panel = useRef<HTMLElement>(null);
  const find = useRef<HTMLInputElement>(null);

  useHistoryKeys({ onFind: () => find.current?.focus(), onEscape: diff.file ? diff.close : null });

  return (
    <section className="view history" aria-labelledby="history-title">
      <div className="bar">
        <h1 id="history-title">History</h1>
        <FindBox ref={find} commits={history.commits} selected={selection.index} onSelect={selection.select} />
        <span className="grow" />
        <span className="history-count" role="status">
          {countText(history)}
        </span>
      </div>
      <ReadError error={history.error} />
      <div className="history-body">
        <div className="history-main">
          <HistoryList history={history} markers={markers} selected={selection.index} onSelect={selection.select} onOpen={() => panel.current?.focus()} now={now} />
          {diff.file && <DiffPane client={client} repoId={state.repoId} file={diff.file} onClose={diff.close} />}
        </div>
        <CommitPanel
          ref={panel}
          state={details}
          markers={markers.get(selection.id ?? "") ?? []}
          onSelect={selection.choose}
          isLoaded={selection.isLoaded}
          openFile={diff.file}
          onOpenFile={diff.open}
        />
      </div>
    </section>
  );
}

function ReadError({ error }: { error: string | null }) {
  if (!error) return null;
  return (
    <p className="empty" role="alert">
      Could not read the history: {error}
    </p>
  );
}

/** The file whose diff is shown. A diff belongs to its commit, so selecting another commit closes it. */
function useOpenFile(commitId: string | null) {
  const [open, setOpen] = useState<{ commitId: string | null; file: FileChange } | null>(null);
  return {
    file: open && open.commitId === commitId ? open.file : null,
    open: (file: FileChange) => setOpen({ commitId, file }),
    close: () => setOpen(null),
  };
}

/**
 * Keys that work wherever the focus is while the history is shown: Ctrl+F finds, instead of the browser's own find,
 * and Escape closes the diff. A control that uses Escape itself stops it first.
 */
function useHistoryKeys({ onFind, onEscape }: { onFind(): void; onEscape: (() => void) | null }) {
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.ctrlKey && event.key.toLowerCase() === "f") {
        event.preventDefault();
        onFind();
      } else if (event.key === "Escape" && onEscape) onEscape();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onFind, onEscape]);
}

/** The selected commit: the first one until the user picks one. Kept by id, so it stays put as pages arrive. */
function useSelection({ commits }: History) {
  const [chosen, setChosen] = useState<string | null>(null);
  const indexById = useMemo(() => new Map(commits.map((c, i) => [c.id, i])), [commits]);
  const id = chosen ?? commits[0]?.id ?? null;
  return {
    id,
    index: id === null ? -1 : (indexById.get(id) ?? -1),
    select: (index: number) => setChosen(commits[index]?.id ?? null),
    choose: setChosen,
    isLoaded: (commitId: string) => indexById.has(commitId),
  };
}

function countText({ commits, done }: History): string {
  const count = commits.length.toLocaleString("en-US");
  if (done) return commits.length === 1 ? "1 commit" : `${count} commits`;
  return commits.length === 0 ? "Reading history…" : `${count} commits so far…`;
}
