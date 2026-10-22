import type { Dispatch, MutableRefObject, SetStateAction } from "react";
import type { AppHost, SidecarEvent } from "../host";
import type { RepoChange, RepoState } from "../protocol";
import { isCancelled, type RpcClient } from "../rpc";
import { folderName, messageOf } from "./format";
import { initialRows, updateRows, type Row } from "./rows";

/** The storage key for the last repo that opened, which opens again on start. */
export const LastRepoKey = "diffract.lastRepo";

export type Screen =
  | { kind: "starting" }
  | { kind: "gitProblem"; problem: string }
  | { kind: "welcome" }
  | { kind: "opening"; path: string }
  | { kind: "ready"; state: RepoState }
  /** `path` is the repo to try again, or null when the sidecar itself is the problem. */
  | { kind: "failed"; title: string; message: string; path: string | null };

/** Shows a message in the notice bar. */
export type ShowNotice = (message: string) => void;

/**
 * What the repo session's actions read and change: the sidecar, the host, React state setters, and refs, because
 * notifications and sidecar events arrive outside React's render cycle.
 */
export interface Session {
  client: RpcClient;
  host: AppHost;
  showNotice: ShowNotice;
  /** The repo on screen. */
  repo: MutableRefObject<{ id: string; path: string } | null>;
  /** Only the newest open may change the screen; a slower earlier one is ignored. */
  openSeq: MutableRefObject<number>;
  /**
   * The last state for a repo that was not on screen. The scan after opening can in principle finish before the
   * answer to repo.open arrives; then this is it, and the open uses it.
   */
  early: MutableRefObject<RepoState | null>;
  setScreen: Dispatch<SetStateAction<Screen>>;
  setRows: Dispatch<SetStateAction<Row[]>>;
  setRefreshing: Dispatch<SetStateAction<boolean>>;
  /** Raising the count starts the app over. */
  setStarts: Dispatch<SetStateAction<number>>;
}

export async function openRepo(session: Session, path: string): Promise<void> {
  const seq = ++session.openSeq.current;
  closeRepo(session);
  session.early.current = null;
  session.setScreen({ kind: "opening", path });
  try {
    const opened = await session.client.call("repo.open", { path });
    if (seq !== session.openSeq.current) return;
    const state = earlyOr(session, opened);
    session.repo.current = { id: state.repoId, path };
    session.host.storage.set(LastRepoKey, path);
    session.setRows(initialRows(state.leftovers));
    session.setScreen({ kind: "ready", state });
  } catch (error) {
    if (seq !== session.openSeq.current) return;
    session.setScreen({ kind: "failed", title: `Could not open ${folderName(path)}`, message: messageOf(error), path });
  }
}

/** The scan's state when it arrived before the answer to repo.open, otherwise that answer. */
function earlyOr(session: Session, opened: RepoState): RepoState {
  const early = session.early.current;
  return early !== null && early.repoId === opened.repoId ? early : opened;
}

function closeRepo(session: Session): void {
  const previous = session.repo.current;
  session.repo.current = null;
  if (previous) session.client.call("repo.close", { repoId: previous.id }).catch(() => {});
}

export async function pickAndOpen(session: Session): Promise<void> {
  const path = await session.host.pickFolder();
  if (path) await openRepo(session, path);
}

export async function retry(session: Session, path: string | null): Promise<void> {
  if (path) return openRepo(session, path);
  await session.host.restartSidecar().catch(() => {});
  restart(session);
}

export function restart(session: Session): void {
  session.setStarts((n) => n + 1);
}

/** `includeUnreachable` also runs `git fsck`, the slowest call by far. */
export async function refreshRepo(session: Session, includeUnreachable: boolean): Promise<void> {
  const current = session.repo.current;
  if (!current) return;
  session.setRefreshing(true);
  try {
    const state = await session.client.call("repo.refresh", { repoId: current.id, includeUnreachable });
    showIfCurrent(session, state);
  } catch (error) {
    if (!isCancelled(error)) session.showNotice(`Refresh failed: ${messageOf(error)}`);
  } finally {
    session.setRefreshing(false);
  }
}

/**
 * A new state for the repo on screen. A state for another repo is kept as `early`, in case it is for the repo being
 * opened. The first full state after opening shows its rows plainly: they are not news, just the scan's result.
 */
export function showIfCurrent(session: Session, state: RepoState, changes: readonly RepoChange[] = []): void {
  if (state.repoId !== session.repo.current?.id) {
    session.early.current = state;
    return;
  }
  const scanned = changes.some((change) => change.kind === "Scanned");
  session.setRows((previous) => (scanned ? initialRows(state.leftovers) : updateRows(previous, state.leftovers)));
  session.setScreen({ kind: "ready", state });
}

/** The sidecar lost its state when it stopped: open the repo again, or start over. */
export function sidecarStopped(session: Session, event: SidecarEvent): void {
  session.client.failAll("The sidecar stopped.");
  const path = session.repo.current?.path ?? null;
  session.repo.current = null;
  if (event.kind === "stopped") {
    session.openSeq.current++;
    session.setScreen({
      kind: "failed",
      title: "The sidecar keeps stopping",
      message: "Diffract's background process stopped several times in a row, so Diffract stopped restarting it.",
      path: null,
    });
    return;
  }
  session.showNotice("Diffract's background process stopped unexpectedly and was restarted.");
  if (path) void openRepo(session, path);
  else restart(session);
}
