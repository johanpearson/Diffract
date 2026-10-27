import type { ToggleName } from "../history/HistoryToggles";
import type { Leftover, LeftoverKind } from "../protocol";

/** The History toggle a leftover's commit needs before History shows it, or null when it always does. */
export function historyToggleFor(kind: LeftoverKind): ToggleName | null {
  if (kind === "Stash") return "stashes";
  if (kind === "CheckpointSession") return "checkpoints";
  return null;
}

/** History lists what refs and worktrees reach, so an orphaned commit, on none of them, is never in it. */
export function canShowInHistory(leftover: Leftover): boolean {
  return leftover.commit !== null && leftover.kind !== "OrphanedCommit";
}

/** A row that can open to list the commits it holds. A folder holds none. */
export function holdsCommits(leftover: Leftover): boolean {
  return leftover.commit !== null && leftover.kind !== "LeftoverFolder";
}
