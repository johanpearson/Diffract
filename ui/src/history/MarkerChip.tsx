import type { Marker } from "./markers";

const kindClass: Record<Marker["kind"], string> = { Head: "head", Branch: "branch", RemoteBranch: "remote", Tag: "tag", Checkpoint: "checkpoint", Stash: "stash" };

/** A branch, tag or HEAD label. The current one is filled; one checked out in a worktree gets ⌂. */
export function MarkerChip({ marker: m, color }: { marker: Marker; color?: string }) {
  const title = m.worktree ? `${m.isCurrent ? "Current, checked out" : "Checked out"} in ${m.worktree}` : undefined;
  return (
    <span className={`marker ${kindClass[m.kind]}${m.isCurrent ? " current" : ""}`} style={color ? { color } : undefined} title={title}>
      {m.name}
      {m.worktree && <span aria-label=", checked out in a worktree"> ⌂</span>}
    </span>
  );
}
