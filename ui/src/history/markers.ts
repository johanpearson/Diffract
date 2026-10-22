import type { HistoryRef, HistoryRefKind, WorktreeInfo } from "../protocol";
import { folderName } from "../model/format";

/** A label on a commit's row: a branch, remote branch, tag, checkpoint or stash, or a detached worktree's HEAD. */
export interface Marker {
  name: string;
  kind: HistoryRefKind | "Head";
  /** Checked out in the main worktree: the repo's current branch or HEAD. */
  isCurrent: boolean;
  /** The worktree folder it is checked out in, or null. */
  worktree: string | null;
}

const order: Record<Marker["kind"], number> = { Head: 0, Branch: 1, RemoteBranch: 2, Tag: 3, Checkpoint: 4, Stash: 5 };

/**
 * The markers for each commit, by commit id. A branch checked out in a worktree is marked with that worktree;
 * a worktree on a detached HEAD gets its own marker. The current one comes first, then branches, remote branches
 * and tags.
 */
export function markersByCommit(refs: readonly HistoryRef[], worktrees: readonly WorktreeInfo[]): Map<string, Marker[]> {
  const result = new Map<string, Marker[]>();
  for (const [commit, marker] of [...refMarkers(refs, worktrees), ...headMarkers(worktrees)]) result.set(commit, [...(result.get(commit) ?? []), marker]);
  for (const list of result.values()) list.sort(compareMarkers);
  return result;
}

/** Each ref, with the worktree its branch is checked out in. */
function refMarkers(refs: readonly HistoryRef[], worktrees: readonly WorktreeInfo[]): [string, Marker][] {
  const byBranch = new Map(worktrees.filter((w) => w.branch !== null).map((w) => [w.branch!, w]));
  return refs.map((ref) => {
    const worktree = ref.kind === "Branch" ? byBranch.get(ref.name) : undefined;
    return [ref.targetId, { name: ref.name, kind: ref.kind, isCurrent: worktree?.isMain ?? false, worktree: worktree?.path ?? null }];
  });
}

/** A marker for each worktree on a detached HEAD. */
function headMarkers(worktrees: readonly WorktreeInfo[]): [string, Marker][] {
  return worktrees
    .filter((w) => w.isDetached && w.head !== null)
    .map((w) => [w.head!, { name: `HEAD (${folderName(w.path)})`, kind: "Head", isCurrent: w.isMain, worktree: w.path }]);
}

function compareMarkers(a: Marker, b: Marker): number {
  return Number(b.isCurrent) - Number(a.isCurrent) || order[a.kind] - order[b.kind] || a.name.localeCompare(b.name);
}
