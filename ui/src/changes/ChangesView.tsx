import { useCallback, useMemo, useState } from "react";
import { Splitter, useStoredSize, type SizeSpec } from "../components/Splitter";
import type { AppHost } from "../host";
import type { Hunk, RepoState, WorktreeChanges, WorktreeInfo } from "../protocol";
import type { RpcClient } from "../rpc";
import { ChangesDiff } from "./ChangesDiff";
import { CommitBox } from "./CommitBox";
import { samePath, worktreeOf } from "../model/worktrees";
import { listsOf, type Entry, type Lists } from "./entries";
import { FileLists } from "./FileLists";
import { RemoteActions, RemoteNotice } from "./RemoteBar";
import { useChanges } from "./useChanges";
import { useRemote } from "./useRemote";

interface Props {
  client: RpcClient;
  state: RepoState;
  /** The folder the repo was opened from. Its worktree is the one shown first. */
  openedPath: string;
  storage: AppHost["storage"];
}

export const ListsWidth: SizeSpec = { key: "diffract.size.changes", initial: 340, min: 240, max: 720 };

/** What is different in one worktree: what to stage, what is staged, the commit box, and the selected file's diff. */
export function ChangesView({ client, state, openedPath, storage }: Props) {
  const worktrees = useMemo(() => state.snapshot.worktrees.filter((w) => !w.isBare && !w.isPrunable), [state.snapshot.worktrees]);
  const [worktree, setWorktree] = useState(() => worktreeOf(openedPath, worktrees) ?? state.repoRoot);

  return (
    <section className="view changes" aria-labelledby="changes-title">
      <ChangesBody key={worktree} client={client} state={state} worktree={worktree} worktrees={worktrees} onWorktree={setWorktree} storage={storage} />
    </section>
  );
}

interface BodyProps extends Omit<Props, "openedPath"> {
  worktree: string;
  worktrees: WorktreeInfo[];
  onWorktree(path: string): void;
}

function ChangesBody({ client, state, worktree, worktrees, onWorktree, storage }: BodyProps) {
  const model = useChangesModel(client, state, worktree);
  const { changes, lists } = model;

  return (
    <>
      <div className="bar">
        <h1 id="changes-title">Changes</h1>
        {changes.status && <BranchInfo status={changes.status} />}
        {changes.status && <RemoteActions status={changes.status} remote={model.remote} disabled={changes.busy || model.warning !== null} />}
        <WorktreePicker worktree={worktree} worktrees={worktrees} onWorktree={onWorktree} />
      </div>
      {model.warning && <InUseWarning reason={model.warning} onAccept={model.accept} />}
      {changes.problem && <Problem message={changes.problem} onDismiss={changes.dismissProblem} />}
      <RemoteNotice remote={model.remote} />
      {changes.status && lists ? <ChangesMain model={model} status={changes.status} lists={lists} storage={storage} /> : <Reading shown={!changes.problem} />}
    </>
  );
}

/** One worktree's changes, the rows selected and what the user may do now. */
function useChangesModel(client: RpcClient, state: RepoState, worktree: string) {
  const changes = useChanges(client, state.repoId, worktree);
  const remote = useRemote(client, state.repoId, worktree, changes.apply);
  const inUse = inUseReason(state, worktree);
  const [accepted, setAccepted] = useState(false);
  const lists = useMemo(() => (changes.status ? listsOf(changes.status, state.snapshot.worktrees) : null), [changes.status, state.snapshot.worktrees]);
  const selection = useSelection(lists);
  const { toggleHunk } = changes;
  const entry = selection.entry;
  const onToggleHunk = useCallback((hunk: Hunk) => entry && toggleHunk(entry, hunk), [entry, toggleHunk]);
  const warning = accepted ? null : inUse;
  return {
    client,
    repoId: state.repoId,
    changes,
    lists,
    selection,
    onToggleHunk,
    warning,
    accept: () => setAccepted(true),
    remote,
    // A pull moves the branch and the index, so nothing is staged or committed while it runs.
    locked: changes.busy || warning !== null || remote.running === "pull",
  };
}

type Model = ReturnType<typeof useChangesModel>;

function ChangesMain({ model, status, lists, storage }: { model: Model; status: WorktreeChanges; lists: Lists; storage: AppHost["storage"] }) {
  const { changes, selection, locked } = model;
  const [width, setWidth] = useStoredSize(storage, ListsWidth);
  const entry = selection.entry;
  return (
    <div className="changes-body" style={{ gridTemplateColumns: `${width}px auto minmax(0, 1fr)` }}>
      <div className="changes-side">
        <FileLists
          lists={lists}
          selected={entry?.key ?? null}
          onSelect={selection.select}
          onToggle={(e) => (e.side === "unstaged" ? changes.stage([e]) : changes.unstage([e]))}
          onStageAll={() => changes.stage(lists.unstaged.filter((e) => e.blocked === null))}
          locked={locked}
        />
        <CommitBox status={status} stagedCount={lists.staged.length} locked={locked} onCommit={changes.commit} />
      </div>
      <Splitter label="Resize file lists" orientation="vertical" spec={ListsWidth} size={width} onSize={setWidth} growsForward />
      {entry ? (
        <ChangesDiff client={model.client} repoId={model.repoId} status={status} entry={entry} storage={storage} onToggleHunk={model.onToggleHunk} locked={locked} />
      ) : (
        <p className="empty">{lists.unstaged.length + lists.staged.length === 0 ? "Nothing has changed in this worktree." : "Select a file to see its changes."}</p>
      )}
    </div>
  );
}

function Problem({ message, onDismiss }: { message: string; onDismiss(): void }) {
  return (
    <div className="notice" role="alert">
      <span>{message}</span>
      <button className="abtn" onClick={onDismiss}>
        Dismiss
      </button>
    </div>
  );
}

function Reading({ shown }: { shown: boolean }) {
  return shown ? <p className="empty">Reading the changes…</p> : null;
}

function BranchInfo({ status }: { status: WorktreeChanges }) {
  return (
    <>
      <span className="ref branch">{status.branch ?? `detached at ${status.headCommit?.slice(0, 8) ?? "nothing yet"}`}</span>
      <span className="muted" role="status">
        {upstreamText(status)}
      </span>
    </>
  );
}

function upstreamText({ upstream, ahead, behind }: WorktreeChanges): string {
  if (upstream === null) return "No upstream";
  if (ahead === null || behind === null) return `${upstream} is gone`;
  if (ahead === 0 && behind === 0) return `Up to date with ${upstream}`;
  const parts = [ahead > 0 ? `${ahead} ahead` : null, behind > 0 ? `${behind} behind` : null].filter((p) => p !== null);
  return `${parts.join(", ")} ${upstream}`;
}

function WorktreePicker({ worktree, worktrees, onWorktree }: { worktree: string; worktrees: WorktreeInfo[]; onWorktree(path: string): void }) {
  if (worktrees.length < 2) return <span className="muted">{worktree}</span>;
  return (
    <label className="worktree-picker">
      <span>Worktree</span>
      <select value={worktree} onChange={(event) => onWorktree(event.target.value)}>
        {worktrees.map((w) => (
          <option key={w.path} value={w.path}>
            {w.branch ? `${w.branch}  ·  ${w.path}` : w.path}
          </option>
        ))}
      </select>
    </label>
  );
}

function InUseWarning({ reason, onAccept }: { reason: string; onAccept(): void }) {
  return (
    <div className="notice warn" role="alert">
      <span>An agent may be working in this worktree: {reason} Staging, committing, pulling or pushing here can mix with its work.</span>
      <button className="abtn" onClick={onAccept}>
        Work here anyway
      </button>
    </div>
  );
}

/** Why the leftover rules say an agent is using this worktree, or null. */
function inUseReason(state: RepoState, worktree: string): string | null {
  const leftover = state.leftovers.find((l) => l.kind === "Worktree" && l.verdict === "InUse" && l.path !== null && samePath(l.path, worktree));
  return leftover?.reason ?? null;
}


/**
 * The selected row, kept by key. When it goes, as after staging it, the same file in the other list is selected if
 * it is there, otherwise the first row.
 */
function useSelection(lists: Lists | null) {
  const [key, setKey] = useState<string | null>(null);
  const all = lists ? [...lists.unstaged, ...lists.staged] : [];
  const entry = all.find((e) => e.key === key) ?? all.find((e) => key !== null && e.file.path === key.slice(key.indexOf(":") + 1)) ?? all[0] ?? null;
  return { entry, select: (e: Entry) => setKey(e.key) };
}
