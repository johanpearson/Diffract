import type { HistoryRef, RefChange, RefNameKind, RepoState, ResetResult, WorktreeInfo } from "../protocol";

/** What the fake's ref methods read and change on the fake sidecar. */
export interface RefsHost {
  refs: HistoryRef[];
  state(repoId: string): RepoState;
  change(repoId: string, update: (state: RepoState) => RepoState): void;
}

type Params = {
  repoId: string;
  worktree: string;
  kind?: RefNameKind;
  name?: string;
  commit?: string;
  switch?: boolean;
  branch?: string;
  remoteBranch?: string;
  mode?: ResetResult["mode"];
  reset?: ResetResult;
};

function refused(message: string) {
  return { code: -32010, message, data: { kind: "ChangeRefused" } };
}

/**
 * branch.*, tag.create and ref.checkName against the fake's refs and worktrees, as the sidecar answers them. Each
 * change moves the refs, then sends repo.changed with a newer snapshot, as the watcher would.
 */
export function answerRefs(host: RefsHost, method: string, p: Params): unknown {
  const handlers: Record<string, () => unknown> = {
    "ref.checkName": () => ({ problem: nameProblem(host.refs, p.kind ?? "Branch", p.name ?? "") }),
    "branch.create": () => createBranch(host, p),
    "tag.create": () => createRef(host, p, "Tag"),
    "branch.switch": () => switchTo(host, p),
    "branch.reset": () => reset(host, p),
    "branch.undoReset": () => undoReset(host, p),
  };
  return handlers[method]?.();
}

export function nameProblem(refs: readonly HistoryRef[], kind: RefNameKind, name: string): string | null {
  const noun = kind === "Branch" ? "branch" : "tag";
  if (name.trim() === "") return `A ${noun} needs a name.`;
  if (/[\s~^:?*[\\]|\.\./.test(name)) return `"${name}" is not a name git allows for a ${noun}.`;
  return refs.some((r) => r.kind === kind && r.name === name) ? `There is already a ${noun} named ${name}.` : null;
}

function createRef(host: RefsHost, p: Params, kind: RefNameKind): RefChange {
  const name = p.name ?? "";
  const problem = nameProblem(host.refs, kind, name);
  if (problem) throw refused(problem);
  host.refs = [...host.refs, { name, kind, targetId: p.commit ?? "", agent: null }];
  touch(host, p, (w) => w);
  const noun = kind === "Branch" ? "branch" : "tag";
  return { ref: `refs/${kind === "Branch" ? "heads" : "tags"}/${name}`, message: `Created ${noun} ${name} at ${(p.commit ?? "").slice(0, 8)}.` };
}

function createBranch(host: RefsHost, p: Params): RefChange {
  const created = createRef(host, p, "Branch");
  if (!p.switch) return created;
  touch(host, p, (w) => ({ ...w, branch: p.name ?? null, head: p.commit ?? null, isDetached: false }));
  return { ...created, message: created.message.replace(/\.$/, ", and switched to it.") };
}

function switchTo(host: RefsHost, p: Params): RefChange {
  if (p.commit) {
    touch(host, p, (w) => ({ ...w, branch: null, head: p.commit!, isDetached: true }));
    return { ref: null, message: `Checked out ${p.commit.slice(0, 8)} on a detached HEAD.` };
  }
  const name = p.branch ?? p.remoteBranch!.slice(p.remoteBranch!.indexOf("/") + 1);
  const target = host.refs.find((r) => r.name === (p.branch ?? p.remoteBranch))!.targetId;
  if (p.remoteBranch) host.refs = [...host.refs, { name, kind: "Branch", targetId: target, agent: null }];
  touch(host, p, (w) => ({ ...w, branch: name, head: target, isDetached: false }));
  return { ref: `refs/heads/${name}`, message: p.branch ? `Switched to ${name}.` : `Created ${name} to follow ${p.remoteBranch}, and switched to it.` };
}

function reset(host: RefsHost, p: Params): ResetResult {
  const branch = worktreeOf(host, p).branch!;
  const ref = host.refs.find((r) => r.kind === "Branch" && r.name === branch)!;
  const result: ResetResult = { branch, oldCommit: ref.targetId, newCommit: p.commit ?? "", mode: p.mode ?? "Soft" };
  move(host, p, branch, result.newCommit);
  return result;
}

function undoReset(host: RefsHost, p: Params): ResetResult {
  const done = p.reset!;
  const ref = host.refs.find((r) => r.kind === "Branch" && r.name === done.branch)!;
  if (ref.targetId !== done.newCommit) throw refused(`${done.branch} is no longer where the reset left it, so undo did nothing. Something has changed it since.`);
  move(host, p, done.branch, done.oldCommit);
  return { ...done, oldCommit: done.newCommit, newCommit: done.oldCommit };
}

function move(host: RefsHost, p: Params, branch: string, commit: string): void {
  host.refs = host.refs.map((r) => (r.kind === "Branch" && r.name === branch ? { ...r, targetId: commit } : r));
  touch(host, p, (w) => ({ ...w, head: commit }));
}

function worktreeOf(host: RefsHost, p: Params): WorktreeInfo {
  return host.state(p.repoId).snapshot.worktrees.find((w) => w.path.toLowerCase() === p.worktree.toLowerCase())!;
}

/** Changes the worktree, and sends repo.changed with a newer snapshot so the history reads what moved. */
function touch(host: RefsHost, p: Params, update: (w: WorktreeInfo) => WorktreeInfo): void {
  const wanted = p.worktree.toLowerCase();
  host.change(p.repoId, (s) => ({
    ...s,
    takenAt: new Date(Date.parse(s.takenAt) + 1000).toISOString(),
    snapshot: { ...s.snapshot, worktrees: s.snapshot.worktrees.map((w) => (w.path.toLowerCase() === wanted ? update(w) : w)) },
  }));
}
