import type { RepoChange, RepoState, WorktreeInfo } from "../protocol";

/** The worktree that holds `path`, the longest match first, as a worktree can sit inside another. */
export function worktreeOf(path: string, worktrees: WorktreeInfo[]): string | null {
  const inside = worktrees.filter((w) => samePath(w.path, path) || normalize(path).startsWith(`${normalize(w.path)}/`));
  return inside.sort((a, b) => b.path.length - a.path.length)[0]?.path ?? null;
}

function normalize(path: string): string {
  return path.replaceAll("\\", "/").replace(/\/+$/, "").toLowerCase();
}

export function samePath(a: string, b: string): boolean {
  return normalize(a) === normalize(b);
}

/** Why an agent seems to be using this worktree, or null when nothing does. */
export function inUseReason(state: RepoState, worktree: string): string | null {
  const leftover = state.leftovers.find((l) => l.kind === "Worktree" && l.verdict === "InUse" && l.path !== null && samePath(l.path, worktree));
  return leftover?.reason ?? null;
}

/**
 * A change to this worktree's files or index, to refs (the upstream may have moved), or one that rebuilt everything.
 * Other worktrees' changes do not concern it.
 */
export function concerns(changes: readonly RepoChange[], worktree: string): boolean {
  return changes.some((c) => (c.path === null ? c.kind !== "WorktreeList" : samePath(c.path, worktree)));
}
