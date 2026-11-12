import type { ChangedFile, CleanupPlanDiscardParams, CleanupPlanView, LeftoverPlanView, RepoChange, RepoState } from "../protocol";
import type { FakeCleanups } from "./fakeCleanup";
import type { FakeWorktree } from "./fakeChanges";
import { lineOf } from "../changes/hunkActions";

/** What the fake's cleanup.planDiscard reads and changes on the fake sidecar. */
export interface DiscardHost {
  worktree(path: string): FakeWorktree;
  state(repoId: string): RepoState;
  change(repoId: string, update: (state: RepoState) => RepoState, changes?: RepoChange[]): void;
  readonly cleanups: FakeCleanups;
}

const undoNote = "Undo puts the file back as it is now, for as long as Diffract stays open.";

/**
 * cleanup.planDiscard as the sidecar answers it: one leftover per file with a single step, worded as the planner words
 * it. Applying throws the changes away on the fake worktree and undoing puts them back, through the fake cleanups.
 */
export class FakeDiscards {
  /** Every cleanup.planDiscard asked for, in order. */
  readonly asked: CleanupPlanDiscardParams[] = [];

  answer(host: DiscardHost, params: CleanupPlanDiscardParams): CleanupPlanView {
    this.asked.push(params);
    const worktree = host.worktree(params.worktree);
    const leftovers = params.hunk ? [this.hunkLeftover(worktree, params.hunk)] : this.fileLeftovers(worktree, params.paths ?? []);
    const paths = params.hunk ? [] : (params.paths ?? []);
    let undo: (() => void) | null = null;
    return host.cleanups.planDiscard(params.repoId, leftovers, {
      apply: () => {
        undo = params.hunk ? worktree.discardHunk(params.hunk.path, params.hunk.id) : worktree.discard(paths);
        host.change(params.repoId, (s) => s);
      },
      undo: () => {
        undo?.();
        host.change(params.repoId, (s) => s);
      },
    });
  }

  private fileLeftovers(worktree: FakeWorktree, paths: readonly string[]): LeftoverPlanView[] {
    return paths.map((path) => {
      const file = unstagedFile(worktree, path);
      const description = file.kind === "Untracked" ? `Delete the new file ${path}` : `Discard all unstaged changes in ${path}`;
      return leftover(path, description);
    });
  }

  private hunkLeftover(worktree: FakeWorktree, { path, id }: { path: string; id: string }): LeftoverPlanView {
    unstagedFile(worktree, path);
    const found = worktree.diff(path, false).hunks.find((h) => h.id === id);
    if (!found) throw { code: -32007, message: `${path} changed since it was shown, so nothing was staged or unstaged. Look at it again.`, data: { kind: "ChangedSinceShown" } };
    return leftover(path, `Discard the change at line ${lineOf(found)} (replaces your ${found.newLines} line with ${found.oldLines} line) in ${path}`);
  }
}

function unstagedFile(worktree: FakeWorktree, path: string): ChangedFile {
  const file = worktree.status.files.find((f) => f.path === path && f.unstaged !== null);
  if (!file) throw { code: -32602, message: `${path} has no unstaged changes to discard. Refresh to see the changes as they are now.` };
  return file;
}

function leftover(path: string, description: string): LeftoverPlanView {
  return {
    kind: "DiscardedChange",
    name: path,
    path,
    refused: null,
    steps: [{ kind: "DiscardChanges", description, undoable: true, undoNote, optional: false, remote: null }],
  };
}
