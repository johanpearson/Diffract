import type { LeftoverKind, Verdict } from "../protocol";

export const verdictLabels: Record<Verdict, string> = {
  SafeToRemove: "Safe to remove",
  NeedsReview: "Needs review",
  InUse: "In use",
};

export const kindLabels: Record<LeftoverKind, string> = {
  Worktree: "Worktree",
  PrunableWorktree: "Worktree, folder gone",
  LocalBranch: "Branch",
  RemoteBranch: "Remote branch",
  Stash: "Stash",
  CheckpointSession: "Checkpoints",
  OrphanedCommit: "Orphaned commits",
  BackupTag: "Backup tag",
  LeftoverFolder: "Folder",
};

/** "-" when the count is unknown, as the milestone asks. */
export function formatAtRisk(count: number | null): string {
  return count === null ? "-" : String(count);
}

/** A short "how long ago": "just now", "5 min ago", "3 h ago", "2 days ago". */
export function formatAge(iso: string, now: Date): string {
  const minutes = Math.floor((now.getTime() - Date.parse(iso)) / 60_000);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} h ago`;
  const days = Math.floor(hours / 24);
  return days === 1 ? "1 day ago" : `${days} days ago`;
}

/** The time of day in the user's own format, with seconds, so a fresh snapshot visibly changes it. */
export function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

/** The last folder in a Windows path: "C:\src\shop" is "shop". */
export function folderName(path: string): string {
  return path.replace(/[\\/]+$/, "").split(/[\\/]/).pop() || path;
}
