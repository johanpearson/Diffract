import type { WorktreeChanges } from "../protocol";

/**
 * The branch checked out, and how it stands against its upstream. `live` makes the second part a status that is read
 * out when it changes; only one place on screen should be.
 */
export function BranchInfo({ status, live = false }: { status: WorktreeChanges; live?: boolean }) {
  return (
    <>
      <span className="ref branch">{status.branch ?? `detached at ${status.headCommit?.slice(0, 8) ?? "nothing yet"}`}</span>
      <span className="muted" role={live ? "status" : undefined}>
        {upstreamText(status)}
      </span>
    </>
  );
}

export function upstreamText({ upstream, ahead, behind }: WorktreeChanges): string {
  if (upstream === null) return "No upstream";
  if (ahead === null || behind === null) return `${upstream} is gone`;
  if (ahead === 0 && behind === 0) return `Up to date with ${upstream}`;
  const parts = [ahead > 0 ? `${ahead} ahead` : null, behind > 0 ? `${behind} behind` : null].filter((p) => p !== null);
  return `${parts.join(", ")} ${upstream}`;
}
