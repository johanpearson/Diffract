import { useCallback, useState } from "react";
import type { AppHost } from "../host";
import type { RepoState } from "../protocol";
import type { RpcClient } from "../rpc";
import type { CurrentWorktree } from "../history/commitMenu";
import { currentWorktree } from "../history/HistoryMenu";
import { useHistoryToggles, type ToggleName, type Toggles } from "../history/HistoryToggles";
import { useHistory, type History } from "../history/useHistory";
import { useRefActions, type RefActions } from "../history/useRefActions";

/** A request to select a commit in History, until History has done it. */
export interface Reveal {
  commitId: string;
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
  actions: RefActions;
  reveal: Reveal | null;
  revealCommit(commitId: string): void;
  /** History selected the commit, so the request is done. */
  revealed(): void;
}

/** With no repo open, nothing is read. */
export function useRepoWork(client: RpcClient, state: RepoState | null, openedPath: string, storage: AppHost["storage"]): RepoWork {
  const [toggles, setToggle] = useHistoryToggles(storage);
  const history = useHistory(client, state?.repoId ?? null, state?.takenAt ?? "", { checkpoints: toggles.checkpoints, stashes: toggles.stashes });
  const current = state ? currentWorktree(state, openedPath, history.refs.refs) : noWorktree;
  const actions = useRefActions(client, state?.repoId ?? "", current.path);
  const [reveal, setReveal] = useState<Reveal | null>(null);
  const revealCommit = useCallback((commitId: string) => setReveal({ commitId }), []);
  const revealed = useCallback(() => setReveal(null), []);
  return { history, toggles, setToggle, current, actions, reveal, revealCommit, revealed };
}

const noWorktree: CurrentWorktree = { path: "", branch: null, head: null };
