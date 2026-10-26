import { useMemo, useState } from "react";
import { ContextMenu } from "../components/ContextMenu";
import { worktreeOf } from "../model/worktrees";
import type { CommitEntry, HistoryRef, RepoState } from "../protocol";
import type { RpcClient } from "../rpc";
import { commitMenuItems, type CurrentWorktree } from "./commitMenu";
import type { Marker } from "./markers";
import { NameDialog, ResetDialog } from "./RefDialogs";
import { resetPreview } from "./resetPreview";
import type { RefActions } from "./useRefActions";

/** A row's open menu: which commit, and where. */
export interface OpenMenu {
  index: number;
  x: number;
  y: number;
}

interface Props {
  client: RpcClient;
  state: RepoState;
  commits: readonly CommitEntry[];
  refs: readonly HistoryRef[];
  markers: ReadonlyMap<string, Marker[]>;
  current: CurrentWorktree;
  actions: RefActions;
  menu: OpenMenu | null;
  onClose(): void;
}

/** The menu on a history row, and the dialog an item opens. */
export function HistoryMenu({ client, state, commits, refs, markers, current, actions, menu, onClose }: Props) {
  const localBranches = useMemo(() => new Set(refs.filter((r) => r.kind === "Branch").map((r) => r.name)), [refs]);
  const commit = menu ? commits[menu.index] : undefined;
  return (
    <>
      {menu && commit && (
        <ContextMenu
          x={menu.x}
          y={menu.y}
          label={`Actions for ${commit.id.slice(0, 8)} ${commit.subject}`}
          onClose={onClose}
          items={commitMenuItems(
            { commit, markers: markers.get(commit.id) ?? [], current, localBranches },
            {
              copy: actions.copy,
              createBranch: (c) => actions.open({ kind: "Branch", commit: c }),
              createTag: (c) => actions.open({ kind: "Tag", commit: c }),
              switchTo: actions.switchTo,
              reset: (c) => actions.open({ kind: "Reset", commit: c }),
            },
          )}
        />
      )}
      <OpenDialog client={client} repoId={state.repoId} commits={commits} refs={refs} current={current} actions={actions} />
    </>
  );
}

function OpenDialog({ client, repoId, commits, refs, current, actions }: Omit<Props, "state" | "markers" | "menu" | "onClose"> & { repoId: string }) {
  const { dialog } = actions;
  if (dialog === null) return null;
  const shared = { busy: actions.busy, onCancel: actions.close };
  if (dialog.kind !== "Reset")
    return (
      <NameDialog
        key={dialog.kind}
        kind={dialog.kind}
        commit={dialog.commit}
        client={client}
        repoId={repoId}
        worktree={current.path}
        onCreate={(name, extra) => (dialog.kind === "Branch" ? actions.createBranch(name, extra.switchTo) : actions.createTag(name, extra.message))}
        {...shared}
      />
    );
  const preview = resetPreview(commits, current.branch ?? "", current.head ?? "", dialog.commit.id, refs);
  return <ResetDialog branch={current.branch ?? ""} commit={dialog.commit} preview={preview} onReset={actions.reset} {...shared} />;
}

/** The row's menu, opened and closed. */
export function useOpenMenu() {
  const [menu, setMenu] = useState<OpenMenu | null>(null);
  return { menu, open: (index: number, at: { x: number; y: number }) => setMenu({ index, ...at }), close: () => setMenu(null) };
}

/**
 * The worktree the repo was opened from, which the menu changes. Its commit comes from the refs history read when it
 * is on a branch, as they are as new as the history shown.
 */
export function currentWorktree(state: RepoState, openedPath: string, refs: readonly HistoryRef[]): CurrentWorktree {
  const worktrees = state.snapshot.worktrees.filter((w) => !w.isBare && !w.isPrunable);
  const path = worktreeOf(openedPath, worktrees) ?? state.repoRoot;
  const info = worktrees.find((w) => w.path === path);
  const branch = info?.branch ?? null;
  const onBranch = refs.find((r) => r.kind === "Branch" && r.name === branch);
  return { path, branch, head: onBranch?.targetId ?? info?.head ?? null };
}

/**
 * What came of copying a hash or subject. Git's own actions say what came of them in the command window. A note that
 * all went well goes by itself; a failure stays until dismissed. The live region is always there, so what appears in
 * it is read out.
 */
export function RefNoteBar({ actions }: { actions: RefActions }) {
  const { note } = actions;
  return (
    <div aria-live="polite">
      {note && (
        <div className={`notice ref-note ${note.tone === "error" ? "error" : "done"}`} role={note.tone === "error" ? "alert" : undefined}>
          <span>{note.text}</span>
          <button className="abtn" onClick={actions.dismiss}>
            Dismiss
          </button>
        </div>
      )}
    </div>
  );
}
