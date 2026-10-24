import type { WorktreeInfo } from "../protocol";

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
