import type { WorktreeChanges } from "../protocol";
import { branchLabel, upstreamText } from "./BranchInfo";

/** What the badge shows: counts when the branch has moved from its upstream, a tick when it has not. */
export function syncBadge({ upstream, ahead, behind }: WorktreeChanges): string {
  if (upstream === null) return "no upstream";
  if (ahead === null || behind === null) return "gone";
  if (ahead === 0 && behind === 0) return "✓";
  return [ahead > 0 ? `↑${ahead}` : null, behind > 0 ? `↓${behind}` : null].filter((p) => p !== null).join(" ");
}

/**
 * The branch and its standing against the upstream, short enough to share a bar. The name is cut with an ellipsis when
 * short of room; the full name and the sentence are the tooltips.
 */
export function BranchChip({ status }: { status: WorktreeChanges }) {
  const name = branchLabel(status);
  const sentence = upstreamText(status);
  return (
    <>
      <span className="ref branch branch-name" title={name}>
        {name}
      </span>
      <span className="sync-badge" role="img" aria-label={sentence} title={sentence}>
        {syncBadge(status)}
      </span>
    </>
  );
}
