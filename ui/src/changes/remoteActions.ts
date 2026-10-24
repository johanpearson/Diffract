import type { RemoteOutcome, WorktreeChanges } from "../protocol";

export type RemoteKind = "fetch" | "pull" | "push";

/** How a result shows: "ok" quietly in the bar, the others as a notice that stays until dismissed. */
export type RemoteTone = "ok" | "warn" | "error";

/** A button's text, and why it cannot run, or null when it can. */
export interface RemoteAction {
  label: string;
  reason: string | null;
}

export const runningText: Record<RemoteKind, string> = { fetch: "Fetching…", pull: "Pulling…", push: "Pushing…" };

/** Diverged and Rejected left the branch as it was, and the user has something to do about it. */
export function toneOf(outcome: RemoteOutcome): RemoteTone {
  return outcome === "Diverged" || outcome === "Rejected" ? "warn" : "ok";
}

/** Pull needs a branch that follows one on a remote. */
export function pullAction({ branch, upstream }: WorktreeChanges): RemoteAction {
  if (branch === null) return { label: "Pull", reason: "No branch is checked out here." };
  if (upstream === null) return { label: "Pull", reason: `${branch} does not follow a branch on a remote yet. Push it first.` };
  return { label: "Pull", reason: null };
}

/**
 * Push says where it goes before it runs: to the upstream, or for a branch without one, to a new branch of the same
 * name on origin. A branch that follows one of another name is left to a terminal, as the sidecar refuses it.
 */
export function pushAction({ branch, headCommit, upstream }: WorktreeChanges): RemoteAction {
  if (branch === null || headCommit === null) return { label: "Push", reason: "No branch with commits is checked out here." };
  if (upstream === null) return { label: `Push to new origin/${branch}`, reason: null };
  if (!upstream.endsWith(`/${branch}`)) return { label: "Push", reason: `${branch} follows ${upstream}, which has another name. Push it from a terminal.` };
  return { label: `Push to ${upstream}`, reason: null };
}
