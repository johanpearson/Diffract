import { useEffect, useMemo, useRef, useState } from "react";
import { Splitter, useStoredSize, type SizeSpec } from "../components/Splitter";
import type { AppHost } from "../host";
import type { RepoWork } from "../model/useRepoWork";
import type { FileChange, HistoryRef, RepoState } from "../protocol";
import type { RpcClient } from "../rpc";
import { CommitPanel } from "./CommitPanel";
import { DiffPane } from "./DiffPane";
import { fadedCommits } from "./emphasis";
import { FindBox } from "./FindBox";
import { HistoryList } from "./HistoryList";
import { HistoryMenu, RefNoteBar, useOpenMenu } from "./HistoryMenu";
import { HistoryToggles, type Toggles } from "./HistoryToggles";
import { markersByCommit } from "./markers";
import { useCommitDetails } from "./useCommitDetails";
import type { History } from "./useHistory";

interface Props {
  client: RpcClient;
  state: RepoState;
  now: Date;
  /** The history, the toggles and the branch actions, shared with the sidebar. */
  work: RepoWork;
  /** Remembers panel sizes and the diff layout. */
  storage: AppHost["storage"];
}

export const DetailsWidth: SizeSpec = { key: "diffract.size.details", initial: 320, min: 220, max: 720 };
export const DiffHeight: SizeSpec = { key: "diffract.size.diff", initial: 380, min: 120, max: 1600 };

/** The repo's history as a graph, with the selected commit's details beside it and a file's diff below. */
export function HistoryView({ client, state, now, work, storage }: Props) {
  const { history, toggles, setToggle, current, actions } = work;
  const refs = useMemo(() => shownRefs(history.refs.refs, toggles), [history.refs.refs, toggles]);
  const markers = useMemo(() => markersByCommit(refs, state.snapshot.worktrees), [refs, state.snapshot.worktrees]);
  const faded = useMemo(
    () => fadedCommits(history.commits, history.refs, state.mainBranch, toggles),
    [history.commits, history.refs, state.mainBranch, toggles],
  );
  const selection = useSelection(history);
  useReveal(work, selection);
  const details = useCommitDetails(client, state.repoId, selection.id);
  const diff = useOpenFile(selection.id);
  const panel = useRef<HTMLElement>(null);
  const find = useRef<HTMLInputElement>(null);
  const [detailsWidth, setDetailsWidth] = useStoredSize(storage, DetailsWidth);
  const menu = useOpenMenu();

  useHistoryKeys({ onFind: () => find.current?.focus(), onEscape: diff.file ? diff.close : null });

  return (
    <section className="view history" aria-labelledby="history-title">
      <div className="bar">
        <h1 id="history-title">History</h1>
        <FindBox ref={find} commits={history.commits} names={markers} selected={selection.index} onSelect={selection.select} />
        <HistoryToggles values={toggles} onChange={setToggle} />
        <span className="grow" />
        <span className="history-count" role="status">
          {countText(history)}
        </span>
      </div>
      <ReadError error={history.error} />
      <RefNoteBar actions={actions} />
      <div className="history-body" style={{ gridTemplateColumns: `minmax(0, 1fr) auto ${detailsWidth}px` }}>
        <div className="history-main">
          <HistoryList history={history} markers={markers} faded={faded} selected={selection.index} onSelect={selection.select} onOpen={() => panel.current?.focus()} onMenu={menu.open} now={now} />
          {diff.file && <DiffArea client={client} repoId={state.repoId} file={diff.file} storage={storage} onClose={diff.close} />}
        </div>
        <Splitter label="Resize commit details" orientation="vertical" spec={DetailsWidth} size={detailsWidth} onSize={setDetailsWidth} growsForward={false} />
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
      <HistoryMenu client={client} state={state} commits={history.commits} refs={history.refs.refs} markers={markers} current={current} actions={actions} menu={menu.menu} onClose={menu.close} />
    </section>
  );
}

/** The diff below the list, with a bar above it to change its height. */
function DiffArea(props: Parameters<typeof DiffPane>[0]) {
  const [height, setHeight] = useStoredSize(props.storage, DiffHeight);
  return (
    <>
      <Splitter label="Resize diff" orientation="horizontal" spec={DiffHeight} size={height} onSize={setHeight} growsForward={false} />
      <div className="diff-frame" style={{ height }}>
        <DiffPane {...props} />
      </div>
    </>
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

/** Checkpoints and stashes are marked only while their toggle is on. */
function shownRefs(refs: HistoryRef[], toggles: Toggles): HistoryRef[] {
  return refs.filter((r) => (r.kind !== "Checkpoint" || toggles.checkpoints) && (r.kind !== "Stash" || toggles.stashes));
}

/**
 * The selected commit: the first one read until the user picks one. Kept by id, so it stays put as pages arrive and
 * as new commits come in above it.
 */
function useSelection({ commits }: History) {
  const [chosen, setChosen] = useState<string | null>(null);
  const first = useRef<string | null>(null);
  first.current ??= commits[0]?.id ?? null;
  const indexById = useMemo(() => new Map(commits.map((c, i) => [c.id, i])), [commits]);
  const id = chosen ?? first.current;
  return {
    id,
    index: id === null ? -1 : (indexById.get(id) ?? -1),
    select: (index: number) => setChosen(commits[index]?.id ?? null),
    choose: setChosen,
    isLoaded: (commitId: string) => indexById.has(commitId),
  };
}

/**
 * Selects the commit asked for from elsewhere, such as the sidebar, once it has been read. A commit the history does
 * not have, such as one only a hidden checkpoint reaches, leaves the selection as it is.
 */
function useReveal({ reveal, revealed, history }: RepoWork, { choose, isLoaded }: ReturnType<typeof useSelection>) {
  const { done } = history;
  useEffect(() => {
    if (!reveal) return;
    const found = isLoaded(reveal.commitId);
    if (found) choose(reveal.commitId);
    if (found || done) revealed();
  }, [reveal, revealed, choose, isLoaded, done]);
}

function countText({ commits, done }: History): string {
  const count = commits.length.toLocaleString("en-US");
  if (done) return commits.length === 1 ? "1 commit" : `${count} commits`;
  return commits.length === 0 ? "Reading history…" : `${count} commits so far…`;
}
