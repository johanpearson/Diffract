import { formatAge } from "../model/format";
import type { LeftoverKind, PastCleanup } from "../protocol";

interface Props {
  history: PastCleanup[];
  busy: boolean;
  onUndo(cleanupId: string): void;
  now: Date;
}

/** One word per kind, for "2 branches, 1 folder". */
const nouns: Record<LeftoverKind, [string, string]> = {
  Worktree: ["worktree", "worktrees"],
  PrunableWorktree: ["worktree", "worktrees"],
  LocalBranch: ["branch", "branches"],
  RemoteBranch: ["remote branch", "remote branches"],
  Stash: ["stash", "stashes"],
  CheckpointSession: ["checkpoint session", "checkpoint sessions"],
  OrphanedCommit: ["orphaned commit row", "orphaned commit rows"],
  BackupTag: ["backup tag", "backup tags"],
  LeftoverFolder: ["folder", "folders"],
};

/**
 * The operation log, folded away under one line. Opened, each past cleanup or undo is one line: when, what it was
 * about, and "Undo" while git can still take it back.
 */
export function PastCleanups({ history, busy, onUndo, now }: Props) {
  if (history.length === 0) return null;
  return (
    <details className="past-cleanups">
      <summary>Past cleanups ({history.length})</summary>
      <ul aria-label="Past cleanups">
        {history.map((c) => (
          <li key={c.cleanupId} className="past-cleanup" aria-label={`${what(c)}, ${formatAge(c.at, now)}`}>
            <span className="when">{formatAge(c.at, now)}</span>
            <span className="what">{what(c)}</span>
            {c.canUndo && (
              <button className="link" disabled={busy} onClick={() => onUndo(c.cleanupId)} aria-label={`Undo the cleanup of ${names(c)}`}>
                Undo
              </button>
            )}
          </li>
        ))}
      </ul>
    </details>
  );
}

/** "2 branches, 1 folder", or "Undo of 2 branches, 1 folder". */
function what(c: PastCleanup): string {
  const counts = new Map<LeftoverKind, number>();
  for (const l of c.leftovers) counts.set(l.kind, (counts.get(l.kind) ?? 0) + 1);
  const parts = [...counts].map(([kind, n]) => `${n} ${nouns[kind][n === 1 ? 0 : 1]}`);
  return c.undoes === null ? parts.join(", ") : `Undo of ${parts.join(", ")}`;
}

function names(c: PastCleanup): string {
  return c.leftovers.map((l) => l.name).join(", ");
}
