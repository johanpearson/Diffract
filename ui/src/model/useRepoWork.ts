import { useCallback, useRef, useState } from "react";
import type { AppHost } from "../host";
import type { RepoState } from "../protocol";
import type { RpcClient } from "../rpc";
import type { CurrentWorktree } from "../history/commitMenu";
import { currentWorktree } from "../history/HistoryMenu";
import { useHistoryToggles, type ToggleName, type Toggles } from "../history/HistoryToggles";
import { useHistory, type History } from "../history/useHistory";
import { useRefActions, type RefActions } from "../history/useRefActions";
import { changesWorktrees, samePath } from "./worktrees";

/** A request to select a commit in History, until History has done it. */
export interface Reveal {
  commitId: string;
  /**
   * Set when a toggle was just turned on to show the commit, such as Stashes: the history at this version is from
   * before, so a commit missing from it may still arrive. Undefined: the history as it is decides.
   */
  afterVersion?: number;
}

/**
 * What History and the sidebar share for the open repo: the history and its refs, read once and kept while other
 * views are shown, the toggles, the branch actions, and a way to select a commit in History from elsewhere.
 */
export interface RepoWork {
  history: History;
  toggles: Toggles;
  setToggle(name: ToggleName, on: boolean): void;
  /** The worktree the repo was opened from, which branch actions act on. */
  current: CurrentWorktree;
  /** The worktree Changes shows, which a stash is applied to. The one the repo was opened from until another is picked. */
  changesWorktree: string;
  setChangesWorktree(path: string): void;
  actions: RefActions;
  reveal: Reveal | null;
  /** `afterUpdate`: a toggle was just turned on, so wait for the history it brings before giving up on the commit. */
  revealCommit(commitId: string, afterUpdate?: boolean): void;
  /** History selected the commit, so the request is done. */
  revealed(): void;
}

/**
 * A reveal is over once the commit is found, or once the whole history has been read without it. After a toggle was
 * turned on for it, the history from before the toggle does not count: only a newer version can rule it out.
 */
export function revealSettled(reveal: Reveal, found: boolean, history: { done: boolean; version: number }): boolean {
  const waiting = reveal.afterVersion !== undefined && history.version <= reveal.afterVersion;
  return found || (history.done && !waiting);
}

/** With no repo open, nothing is read. */
export function useRepoWork(client: RpcClient, state: RepoState | null, openedPath: string, storage: AppHost["storage"]): RepoWork {
  const [toggles, setToggle] = useHistoryToggles(storage);
  const history = useHistory(client, state?.repoId ?? null, state?.takenAt ?? "", { checkpoints: toggles.checkpoints, stashes: toggles.stashes });
  const current = state ? currentWorktree(state, openedPath, history.refs.refs) : noWorktree;
  const actions = useRefActions(client, state?.repoId ?? "", current.path);
  const [chosen, setChangesWorktree] = useState<string | null>(null);
  // A worktree picked in another repo, or one that has gone since, falls back to where the repo was opened.
  const changesWorktree = state && chosen !== null && changesWorktrees(state).some((w) => samePath(w.path, chosen)) ? chosen : current.path;
  const [reveal, setReveal] = useState<Reveal | null>(null);
  const version = useRef(history.version);
  version.current = history.version;
  const revealCommit = useCallback(
    (commitId: string, afterUpdate = false) => setReveal({ commitId, afterVersion: afterUpdate ? version.current : undefined }),
    [],
  );
  const revealed = useCallback(() => setReveal(null), []);
  return { history, toggles, setToggle, current, changesWorktree, setChangesWorktree, actions, reveal, revealCommit, revealed };
}

const noWorktree: CurrentWorktree = { path: "", branch: null, head: null };
