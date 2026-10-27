import type { HistoryRef, RepoChange, RepoState, StashApplyResult, StashPushResult } from "../protocol";
import type { FakeCleanups } from "./fakeCleanup";
import type { FakeWorktree } from "./fakeChanges";

/** What the fake's stash methods read and change on the fake sidecar. */
export interface StashHost {
  refs: HistoryRef[];
  worktree(path: string): FakeWorktree;
  state(repoId: string): RepoState;
  change(repoId: string, update: (state: RepoState) => RepoState, changes?: RepoChange[]): void;
  readonly cleanups: FakeCleanups;
}

type Params = { repoId: string; worktree?: string; message?: string | null; includeUntracked?: boolean; commit?: string };

/**
 * stash.push, stash.apply and cleanup.planStash as the sidecar answers them. A stash is a Stash ref on the fake, at
 * stash@{0}, moving the others down. Dropping goes through the fake cleanups, so it is undone as any cleanup is.
 */
export class FakeStashes {
  /** Every stash.* and cleanup.planStash asked for, in order. */
  readonly asked: { method: string; params: unknown }[] = [];
  /** Stash commits whose apply ends in conflicts. */
  readonly conflicts = new Set<string>();
  private next = 0;

  answer(host: StashHost, method: string, params: unknown): unknown {
    this.asked.push({ method, params });
    const p = params as Params;
    if (method === "stash.push") return this.push(host, p);
    if (method === "stash.apply") return this.apply(host, p);
    return this.planDrop(host, p);
  }

  private push(host: StashHost, p: Params): StashPushResult {
    const worktree = host.worktree(p.worktree ?? "");
    // Git stashes changes to tracked files, new files only when asked, and never a nested repo.
    const kept = worktree.status.files.filter((f) => f.kind !== "Tracked" && !(f.kind === "Untracked" && p.includeUntracked));
    if (kept.length === worktree.status.files.length) throw { code: -32010, message: "There is nothing to stash.", data: { kind: "ChangeRefused" } };
    worktree.status = { ...worktree.status, files: kept };
    const branch = worktree.status.branch ?? "(no branch)";
    const subject = (p.message ?? "").split("\n")[0]!.trim();
    const commitId = (++this.next).toString(16).padStart(40, "5");
    const stash: HistoryRef = { name: "stash@{0}", kind: "Stash", targetId: commitId, agent: null, message: subject ? `On ${branch}: ${subject}` : `WIP on ${branch}: work in progress` };
    this.setStashes(host, p.repoId, [stash, ...stashesOf(host)]);
    return { commitId, message: "Stashed your changes. The stash keeps them until you drop it.", status: worktree.status };
  }

  private apply(host: StashHost, p: Params): StashApplyResult {
    const worktree = host.worktree(p.worktree ?? "");
    if (!this.conflicts.has(p.commit ?? "")) return { conflicts: false, message: "Applied the stash. It is still in the list.", status: worktree.status };
    const conflicted = { path: "src/räksmörgås.cs", oldPath: null, kind: "Conflicted" as const, staged: null, unstaged: null, headId: null, indexId: null };
    worktree.status = { ...worktree.status, files: [...worktree.status.files, conflicted] };
    return { conflicts: true, message: "Applied the stash with conflicts. It is still in the list, and Changes lists the conflicted files.", status: worktree.status };
  }

  private planDrop(host: StashHost, p: Params) {
    const stash = stashesOf(host).find((s) => s.targetId === p.commit);
    if (!stash) throw { code: -32011, message: "This stash is no longer in the list. Refresh to see the stashes as they are now.", data: { kind: "CleanupRefused" } };
    return host.cleanups.planStash(p.repoId, stash, {
      apply: () => this.setStashes(host, p.repoId, stashesOf(host).filter((s) => s.targetId !== stash.targetId)),
      undo: () => this.setStashes(host, p.repoId, [stash, ...stashesOf(host)]),
    });
  }

  /** Renumbers the stashes from the top, and tells the UI the refs moved, as the watcher would. */
  private setStashes(host: StashHost, repoId: string, stashes: HistoryRef[]): void {
    const numbered = stashes.map((s, i) => ({ ...s, name: `stash@{${i}}` }));
    host.refs = [...host.refs.filter((r) => r.kind !== "Stash"), ...numbered];
    host.change(repoId, (s) => ({ ...s, takenAt: new Date(Date.parse(s.takenAt) + 1000).toISOString() }));
  }
}

function stashesOf(host: StashHost): HistoryRef[] {
  return host.refs.filter((r) => r.kind === "Stash");
}
