import type { CommitEntry, HistoryRef } from "../protocol";

/** What a reset of the current branch to another commit would do to it, worked out from the history shown. */
export interface ResetPreview {
  /** The commits that leave the branch, newest first. */
  leaving: CommitEntry[];
  /** How many commits come onto the branch, when the target is not one of its own commits. */
  arriving: number;
  /** A remote-tracking branch that already has some of the leaving commits, or null. Pushing after needs a force push. */
  pushedTo: string | null;
}

/** The commits `tips` reach through their parents, among those the history has. */
export function reachable(commits: readonly CommitEntry[], tips: readonly string[]): Set<string> {
  const parents = new Map(commits.map((c) => [c.id, c.parentIds]));
  const reached = new Set<string>();
  const stack = tips.filter((t) => parents.has(t));
  while (stack.length > 0) {
    const id = stack.pop()!;
    if (reached.has(id)) continue;
    reached.add(id);
    stack.push(...parents.get(id)!.filter((p) => parents.has(p) && !reached.has(p)));
  }
  return reached;
}

/**
 * Leaving: what the branch reaches now and the target does not. Arriving: the other way round. A remote branch is
 * named when it has a leaving commit: the branch's own upstream if it looks like one, otherwise the first that does.
 */
export function resetPreview(commits: readonly CommitEntry[], branch: string, head: string, target: string, refs: readonly HistoryRef[]): ResetPreview {
  const fromHead = reachable(commits, [head]);
  const fromTarget = reachable(commits, [target]);
  const leaving = commits.filter((c) => fromHead.has(c.id) && !fromTarget.has(c.id));
  const arriving = [...fromTarget].filter((id) => !fromHead.has(id)).length;
  return { leaving, arriving, pushedTo: pushedTo(commits, branch, leaving, refs) };
}

function pushedTo(commits: readonly CommitEntry[], branch: string, leaving: readonly CommitEntry[], refs: readonly HistoryRef[]): string | null {
  const remotes = refs.filter((r) => r.kind === "RemoteBranch").sort((a, b) => Number(b.name.endsWith(`/${branch}`)) - Number(a.name.endsWith(`/${branch}`)));
  return remotes.find((r) => {
    const onRemote = reachable(commits, [r.targetId]);
    return leaving.some((c) => onRemote.has(c.id));
  })?.name ?? null;
}
