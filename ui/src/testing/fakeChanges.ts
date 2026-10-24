import type { ChangedFile, ChangesFile, Hunk, RemoteSyncResult, WorktreeChanges } from "../protocol";

type Side = "unstaged" | "staged";

/** Thrown as the sidecar's own errors are: a JSON-RPC error with Diffract's kind. */
function error(code: number, message: string, kind: string) {
  return { code, message, data: { kind } };
}

const cartBefore = ["namespace Shop;", "", "public class Cart", "{", "    public decimal Total;", "", "    public void Add(decimal price)", "    {", "        Total += price;", "    }", "}", ""].join("\n");
const cartAfter = cartBefore.replace("public decimal Total;", "public decimal Total { get; private set; }").replace("Total += price;", "Total += Math.Round(price, 2);");

/** Two worktrees of the sample repo, each with a few changes, as the sidecar's changes.* methods see them. */
export function sampleWorktrees(): Map<string, FakeWorktree> {
  const main: WorktreeChanges = {
    worktree: "C:\\src\\shop",
    branch: "main",
    headCommit: "4b825dc642cb6eb9a060e54bf8d69288fbee4904",
    upstream: "origin/main",
    ahead: 1,
    behind: 0,
    headOnRemote: null,
    files: [
      file("README.md", { staged: "Modified", headId: "aa".repeat(20), indexId: "ab".repeat(20) }),
      file("src/Cart.cs", { unstaged: "Modified", headId: "ca".repeat(20), indexId: "ca".repeat(20) }),
      file(".claude/worktrees/räksmörgås fix", { kind: "NestedRepo", unstaged: "Added" }),
      file("docs/notes.md", { kind: "Untracked", unstaged: "Added" }),
    ],
  };
  const agent: WorktreeChanges = {
    ...main,
    worktree: "C:\\src\\shop\\.claude\\worktrees\\räksmörgås fix",
    branch: "claude/räksmörgås-fix",
    upstream: null,
    ahead: null,
    behind: null,
    files: [file("src/räksmörgås.cs", { unstaged: "Modified", headId: "da".repeat(20), indexId: "da".repeat(20) })],
  };
  const diffs = new Map<string, ChangesFile>([
    ["unstaged:src/Cart.cs", { old: text(cartBefore), new: text(cartAfter), hunks: [hunk("cart-total", 5), hunk("cart-round", 9)] }],
    ["unstaged:docs/notes.md", { old: null, new: text("# Notes\n"), hunks: [] }],
    ["staged:README.md", { old: text("Shop\n"), new: text("Shop, with a cart\n"), hunks: [hunk("readme", 1)] }],
  ]);
  return new Map([
    [main.worktree.toLowerCase(), new FakeWorktree(main, diffs)],
    [agent.worktree.toLowerCase(), new FakeWorktree(agent, new Map())],
  ]);
}

function file(path: string, fields: Partial<ChangedFile>): ChangedFile {
  return { path, oldPath: null, kind: "Tracked", staged: null, unstaged: null, headId: null, indexId: null, ...fields };
}

const text = (value: string) => ({ kind: "Text" as const, text: value, message: null });

function hunk(id: string, line: number): Hunk {
  return { id, oldStart: line, oldLines: 1, newStart: line, newLines: 1 };
}

/**
 * One worktree in memory. Staging moves a file, or one hunk of its diff, from the unstaged side to the staged one;
 * unstaging moves it back; committing empties the staged side. Diffs not set up answer a one-line text.
 */
export class FakeWorktree {
  /** Every commit made here, newest last. */
  readonly commits: { message: string; amend: boolean }[] = [];

  constructor(
    public status: WorktreeChanges,
    private readonly diffs: Map<string, ChangesFile>,
  ) {}

  diff(path: string, staged: boolean): ChangesFile {
    const side: Side = staged ? "staged" : "unstaged";
    return this.diffs.get(`${side}:${path}`) ?? { old: text(`${path} before\n`), new: text(`${path} after\n`), hunks: [] };
  }

  stage(paths: readonly string[]): WorktreeChanges {
    return this.update((f) => (paths.includes(f.path) && f.unstaged !== null ? this.move(f, "unstaged") : f));
  }

  unstage(paths: readonly string[]): WorktreeChanges {
    return this.update((f) => (paths.includes(f.path) && f.staged !== null ? this.move(f, "staged") : f));
  }

  /** Moves one hunk across, or answers as the sidecar does when the file changed since its diff was read. */
  moveHunk(path: string, id: string, from: Side): WorktreeChanges {
    const source = this.diffs.get(`${from}:${path}`);
    const moved = source?.hunks.find((h) => h.id === id);
    if (!source || !moved) throw error(-32007, `${path} changed since it was shown, so nothing was staged or unstaged. Look at it again.`, "ChangedSinceShown");
    const to: Side = from === "unstaged" ? "staged" : "unstaged";
    const target = this.diffs.get(`${to}:${path}`) ?? { ...source, hunks: [] };
    this.diffs.set(`${to}:${path}`, { ...target, hunks: [...target.hunks, moved] });
    const left = source.hunks.filter((h) => h !== moved);
    this.diffs.set(`${from}:${path}`, { ...source, hunks: left });
    return this.update((f) => (f.path === path ? { ...f, [to]: f[to] ?? "Modified", [from]: left.length === 0 ? null : f[from] } : f));
  }

  commit(message: string, amend: boolean): WorktreeChanges {
    if (!amend && !this.status.files.some((f) => f.staged !== null)) throw error(-32008, "Nothing is staged, so there is nothing to commit.", "CommitRefused");
    this.commits.push({ message, amend });
    const head = (this.commits.length + 0xc0ffee).toString(16).padStart(40, "0");
    this.status = { ...this.status, headCommit: head, ahead: (this.status.ahead ?? 0) + (amend ? 0 : 1) };
    return this.update((f) => (f.staged === null ? f : { ...f, staged: null }));
  }

  /** Fetch, pull and push as the sidecar decides them, from the branch, upstream and counts. */
  remote(kind: "fetch" | "pull" | "push"): RemoteSyncResult {
    const { branch, upstream, ahead, behind } = this.status;
    if (kind === "fetch") return this.answer("Done", "Fetched from origin.");
    if (branch === null) throw error(-32009, `No branch with commits is checked out here, so there is nothing to ${kind}.`, "RemoteFailed");
    return kind === "pull" ? this.pull(branch, upstream, ahead ?? 0, behind ?? 0) : this.push(branch, upstream, ahead ?? 0, behind ?? 0);
  }

  private pull(branch: string, upstream: string | null, ahead: number, behind: number): RemoteSyncResult {
    if (upstream === null) throw error(-32009, `${branch} does not follow a branch on a remote, so there is nothing to pull. Push it first.`, "RemoteFailed");
    if (behind === 0) return this.answer("UpToDate", `Nothing to pull. ${branch} has everything on ${upstream}.`);
    if (ahead > 0) return this.answer("Diverged", `${branch} and ${upstream} have both moved on. Pull only fast-forwards for now, so nothing was changed.`);
    this.status = { ...this.status, behind: 0 };
    return this.answer("Done", `Pulled ${behind} commits from ${upstream}.`);
  }

  private push(branch: string, upstream: string | null, ahead: number, behind: number): RemoteSyncResult {
    const target = upstream ?? `origin/${branch}`;
    if (behind > 0) return this.answer("Rejected", `${target} has commits that ${branch} lacks, so git did not push. Pull first, then push.`);
    if (upstream !== null && ahead === 0) return this.answer("UpToDate", `Nothing to push. ${target} already has everything on ${branch}.`);
    this.status = { ...this.status, upstream: target, ahead: 0, behind: 0, headOnRemote: target };
    return this.answer("Done", upstream === null ? `Pushed ${branch} to ${target}, which it now follows.` : `Pushed ${branch} to ${target}.`);
  }

  private answer(outcome: RemoteSyncResult["outcome"], message: string): RemoteSyncResult {
    return { outcome, message, status: this.status };
  }

  private move(f: ChangedFile, from: Side): ChangedFile {
    const to: Side = from === "unstaged" ? "staged" : "unstaged";
    const diff = this.diffs.get(`${from}:${f.path}`);
    if (diff) {
      this.diffs.set(`${to}:${f.path}`, diff);
      this.diffs.delete(`${from}:${f.path}`);
    }
    const kind = from === "unstaged" && f.kind === "Untracked" ? "Tracked" : to === "unstaged" && f.staged === "Added" ? "Untracked" : f.kind;
    return { ...f, kind, [to]: f[to] ?? f[from], [from]: null };
  }

  /** Changes each file, and drops those with nothing left to show. */
  private update(change: (f: ChangedFile) => ChangedFile): WorktreeChanges {
    const files = this.status.files.map(change).filter((f) => f.staged !== null || f.unstaged !== null || f.kind === "Conflicted");
    this.status = { ...this.status, files };
    return this.status;
  }
}
