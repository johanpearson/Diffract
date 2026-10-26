import detailsSample from "../../../tests/Diffract.Sidecar.Tests/Contract/history.details.json";
import pageSample from "../../../tests/Diffract.Sidecar.Tests/Contract/history.page.json";
import refsSample from "../../../tests/Diffract.Sidecar.Tests/Contract/history.refs.json";
import openSample from "../../../tests/Diffract.Sidecar.Tests/Contract/repo.open.json";
import type {
  AppInfo,
  CommitDetails,
  CommitEntry,
  GitOutputKind,
  GitInUse,
  GitUpdateCheck,
  GitUpdateResult,
  HistoryFile,
  HistoryPage,
  HistoryRef,
  HistoryRefs,
  HistoryShow,
  HistoryUpdateResult,
  Methods,
  RepoChange,
  RepoState,
  TerminalInfo,
  TerminalKind,
} from "../protocol";
import { sampleWorktrees, type FakeWorktree } from "./fakeChanges";
import { answerRefs } from "./fakeRefs";
import { detailsOf, generateHistory, sampleFiles } from "./fakeHistory";
import { FakeTransport } from "./fakeTransport";

/** The repo in the repo.open contract sample, which the sidecar's own tests write. */
export const sampleState = (openSample as { result: RepoState }).result;

/** The history in the contract samples: a merge whose second parent is not in it, and a root commit. */
export const sampleCommits = (pageSample as { params: HistoryPage }).params.commits;
export const sampleRefs = (refsSample as { result: HistoryRefs }).result;
export const sampleDetails = (detailsSample as { result: CommitDetails }).result;

/** Commits per history.page, as the sidecar sends them. */
export const HistoryPageSize = 500;

export const supportedGit: AppInfo = { sidecarVersion: "0.1.0", gitVersion: "2.47.1", gitSupported: true, gitProblem: null };

type Answer = { error: { code: number; message: string; data?: { kind: string } } } | { result: unknown };

/**
 * A sidecar in memory, answering the real protocol with canned repos. Used by component tests and by the
 * Playwright build, which runs the real UI in a browser without Tauri.
 */
export class FakeSidecar {
  readonly transport: FakeTransport;
  info: AppInfo = supportedGit;
  /** Repos by path, lower case. */
  readonly repos = new Map<string, RepoState>([[sampleState.repoRoot.toLowerCase(), sampleState]]);
  readonly openedFolders: string[] = [];
  /** Every terminal opened, as "path" for the default one or "path in Kind" for a chosen one. */
  readonly openedTerminals: string[] = [];
  terminals: TerminalInfo[] = [
    { id: "WindowsTerminal", name: "Windows Terminal" },
    { id: "WindowsPowerShell", name: "Windows PowerShell" },
    { id: "GitBash", name: "Git Bash" },
  ];
  gitUpdate: GitUpdateCheck = {
    status: "UpToDate",
    installedVersion: "2.47.1",
    availableVersion: null,
    problem: null,
    downloadPage: "https://gitforwindows.org/",
  };
  /** How the next git.update ends. */
  gitUpdateResult: GitUpdateResult = { outcome: "Updated", message: "Git for Windows 2.55.0.5 is installed.", inUse: [] };
  /** What git.inUse answers. */
  gitInUse: GitInUse[] = [];
  /** Every version git.update was asked to install. */
  readonly gitUpdates: string[] = [];
  downloadPageOpened = 0;
  /** Requests with these methods get no answer until `release` is called, like a slow sidecar. */
  readonly held = new Set<keyof Methods>();
  /** Set to make the next request to that method fail. */
  readonly failures = new Map<keyof Methods, Answer>();
  /**
   * Every commit the repo has, newest first in topological order. history.read streams those its refs reach, and
   * history.update works out what changed from them, so a test changes the repo by changing these and `refs`.
   */
  history: CommitEntry[] = sampleCommits;
  /** The refs history.refs answers with. Their targets, and the worktree HEADs, are the tips. */
  refs: HistoryRef[] = sampleRefs.refs;
  agentHeads: string[] = sampleRefs.agentHeads;
  agentSubjectPrefixes: string[] = sampleRefs.agentSubjectPrefixes;
  /** When true, the next history.update answers that the whole history must be read again. */
  rereadNext = false;
  /** The params of every history.update. */
  readonly updatesAsked: { tips: string[]; show: HistoryShow }[] = [];
  /** Every commit history.details was asked for. */
  readonly detailsAsked: string[] = [];
  /** What history.file answers, by blob id. Any other blob is a one-line text naming it. */
  readonly files = new Map<string, HistoryFile>(sampleFiles);
  /** Every blob history.file was asked for. */
  readonly filesAsked: string[] = [];
  /** The changes of each worktree, by path, lower case. changes.* reads and changes them. */
  readonly worktreeChanges = sampleWorktrees();
  /** The params of every changes.stage and changes.unstage, in order. */
  readonly stagesAsked: { method: string; paths?: string[]; hunk?: { path: string; id: string } }[] = [];

  private readonly waiting: (() => void)[] = [];

  constructor() {
    this.transport = new FakeTransport((method, params) => this.answer(method as keyof Methods, params));
  }

  /** The state of the open repo with this id. */
  state(repoId: string): RepoState {
    const state = [...this.repos.values()].find((s) => s.repoId === repoId);
    if (!state) throw new Error(`No repo ${repoId}`);
    return state;
  }

  /** Replaces a repo's state and sends repo.changed, as the watcher does after a refresh. */
  change(repoId: string, update: (state: RepoState) => RepoState, changes: RepoChange[] = [{ kind: "Refs", path: null }]): void {
    const state = update(this.state(repoId));
    this.repos.set(state.repoRoot.toLowerCase(), state);
    this.transport.emit(JSON.stringify({ jsonrpc: "2.0", method: "repo.changed", params: { changes, state } }));
  }

  /**
   * When true, repo.open answers as the real sidecar does for a repo it has not scanned yet: refs and worktrees, no
   * leftovers, `scanning`. Then `finishScan` or `failScan` sends the scan's repo.changed. When false, it answers in full.
   */
  scanOnOpen = false;

  /** Sends the scan's result for a repo opened with `scanOnOpen`. */
  finishScan(repoId: string): void {
    this.change(repoId, (s) => s, [{ kind: "Scanned", path: null }]);
  }

  /** Sends a failed scan for a repo opened with `scanOnOpen`. */
  failScan(repoId: string, problem: string): void {
    const state = { ...scanningState(this.state(repoId)), scanning: false, scanProblem: problem };
    this.transport.emit(JSON.stringify({ jsonrpc: "2.0", method: "repo.changed", params: { changes: [{ kind: "Scanned", path: null }], state } }));
  }

  /** Replaces the history with a generated one of `count` commits, with its branches and a tag. */
  useGeneratedHistory(count: number): void {
    ({ commits: this.history, refs: this.refs } = generateHistory(count));
  }

  /** As if `count` commits were made on main: they go on top of the history, main moves, and repo.changed follows. */
  commitOnMain(repoId: string, count: number, subject?: string): void {
    const main = this.refs.find((r) => r.kind === "Branch" && r.name === "main")!;
    let parent = main.targetId;
    const added: CommitEntry[] = [];
    for (let i = 0; i < count; i++) {
      const at = new Date(Date.parse("2026-10-21T09:00:00+02:00") + (++this.newCommits) * 1000).toISOString();
      const id = this.newCommits.toString(16).padStart(40, "f");
      added.unshift({ id, parentIds: [parent], authorName: "Claude", authorEmail: "noreply@anthropic.com", authoredAt: at, committedAt: at, subject: subject ?? `New commit ${this.newCommits}` });
      parent = id;
    }
    this.history = [...added, ...this.history];
    this.refs = this.refs.map((r) => (r === main ? { ...r, targetId: parent } : r));
    this.change(repoId, (s) => ({ ...s, takenAt: new Date(Date.parse(s.takenAt) + this.newCommits * 1000).toISOString() }));
  }

  /** The changes of one worktree. */
  worktree(path: string): FakeWorktree {
    const worktree = this.worktreeChanges.get(path.toLowerCase());
    if (!worktree) throw { code: -32602, message: `Not a worktree of this repo with files to show: ${path}` };
    return worktree;
  }

  private changes(method: keyof Methods, params: unknown): unknown {
    const p = params as { repoId: string; worktree: string; path?: string; staged?: boolean; paths?: string[]; hunk?: { path: string; id: string }; message?: string; amend?: boolean };
    const worktree = this.worktree(p.worktree);
    switch (method) {
      case "changes.status":
        return worktree.status;
      case "changes.file":
        return worktree.diff(p.path ?? "", p.staged ?? false);
      case "changes.stage":
      case "changes.unstage": {
        this.stagesAsked.push({ method, paths: p.paths, hunk: p.hunk });
        const from = method === "changes.stage" ? "unstaged" : "staged";
        if (p.hunk) return worktree.moveHunk(p.hunk.path, p.hunk.id, from);
        return from === "unstaged" ? worktree.stage(p.paths ?? []) : worktree.unstage(p.paths ?? []);
      }
      case "changes.commit": {
        const status = worktree.commit(p.message ?? "", p.amend ?? false);
        // A new commit on main shows at the top of History, as the watcher would make it.
        if (status.branch === "main" && !p.amend) this.commitOnMain(p.repoId, 1, (p.message ?? "").split("\n")[0]);
        return { commitId: status.headCommit, status };
      }
    }
    return undefined;
  }

  /** Every ref.checkName, branch.* and tag.create asked for, with its params, in order. */
  readonly refsAsked: { method: string; params: unknown }[] = [];

  /** Every fetch, pull and push asked for, as "remote.pull C:\\src\\shop". */
  readonly remoteAsked: string[] = [];

  private remote(method: "remote.fetch" | "remote.pull" | "remote.push", params: { repoId: string; worktree: string }) {
    this.remoteAsked.push(`${method} ${params.worktree}`);
    const answer = this.worktree(params.worktree).remote(method.slice("remote.".length) as "fetch" | "pull" | "push");
    // Remote-tracking refs moved, so the watcher reports it after the answer.
    queueMicrotask(() => this.change(params.repoId, (s) => s));
    return answer;
  }

  private newCommits = 0;

  release(): void {
    this.held.clear();
    for (const answer of this.waiting.splice(0)) answer();
  }

  /** What git "says" during the next request that names an operation, after the command line. */
  gitSays: { kind: GitOutputKind; text: string }[] = [];

  /** As the sidecar does for a request that names an operation: the command, then git's lines, before the answer. */
  private watch(method: keyof Methods, params: unknown): void {
    const operation = (params as { operation?: string } | undefined)?.operation;
    if (!operation) return;
    const lines = [{ kind: "Command" as const, text: `git ${method.slice(method.indexOf(".") + 1)}` }, ...this.gitSays.splice(0)];
    for (const line of lines) this.transport.emit(JSON.stringify({ jsonrpc: "2.0", method: "command.output", params: { operation, ...line } }));
  }

  private answer(method: keyof Methods, params: unknown): unknown {
    this.watch(method, params);
    const failure = this.failures.get(method);
    if (failure) {
      this.failures.delete(method);
      if ("error" in failure) throw failure.error;
      return failure.result;
    }
    if (this.held.has(method)) {
      const id = this.lastRequestId();
      this.waiting.push(() => this.transport.emit(JSON.stringify({ jsonrpc: "2.0", id, result: this.respond(method, params) })));
      return undefined;
    }
    if (method === "history.read") return this.streamHistory(params as { token: string } & HistoryShow);
    return this.respond(method, params);
  }

  // The transport records a request before asking for its answer, so the last one sent is this one.
  private lastRequestId(): number | undefined {
    return this.transport.sent.at(-1)?.id;
  }

  /** The commits history starts from: what the refs shown point at, and every worktree's HEAD. */
  private tips(show: HistoryShow): string[] {
    const shown = this.refs.filter((r) => (r.kind !== "Checkpoint" || show.checkpoints) && (r.kind !== "Stash" || show.stashes));
    const heads = [...this.repos.values()].flatMap((s) => s.snapshot.worktrees.map((w) => w.head)).filter((h) => h !== null);
    return [...new Set([...shown.map((r) => r.targetId), ...heads])];
  }

  /** The ids of the commits `tips` reach, through the parents the fake history has. */
  private reach(tips: string[]): Set<string> {
    const byId = new Map(this.history.map((c) => [c.id, c]));
    const reached = new Set<string>();
    const stack = tips.filter((t) => byId.has(t));
    while (stack.length > 0) {
      const id = stack.pop()!;
      if (reached.has(id)) continue;
      reached.add(id);
      stack.push(...byId.get(id)!.parentIds.filter((p) => byId.has(p)));
    }
    return reached;
  }

  /** As the sidecar does: the commits the new tips reach that the old ones did not, on top, and those no longer reached. */
  private updateHistory({ tips, checkpoints, stashes }: { tips: string[] } & HistoryShow): HistoryUpdateResult {
    const show = { checkpoints, stashes };
    this.updatesAsked.push({ tips, show });
    const now = this.tips(show);
    if (this.rereadNext) {
      this.rereadNext = false;
      return { added: [], removed: [], tips: now, reread: true };
    }
    const before = this.reach(tips);
    const after = this.reach(now);
    return {
      added: this.history.filter((c) => after.has(c.id) && !before.has(c.id)),
      removed: [...before].filter((id) => !after.has(id)),
      tips: now,
      reread: false,
    };
  }

  /** Sends the history page by page, each in its own task as from a real pipe, then the totals. */
  private streamHistory({ token, ...show }: { token: string } & HistoryShow): undefined {
    const id = this.lastRequestId();
    const tips = this.tips(show);
    const reached = this.reach(tips);
    const commits = this.history.filter((c) => reached.has(c.id));
    const pages = Math.ceil(commits.length / HistoryPageSize);
    const send = (index: number) => {
      if (index === pages) {
        this.transport.emit(JSON.stringify({ jsonrpc: "2.0", id, result: { commits: commits.length, pages, tips } }));
        return;
      }
      const page: HistoryPage = { token, index, commits: commits.slice(index * HistoryPageSize, (index + 1) * HistoryPageSize) };
      this.transport.emit(JSON.stringify({ jsonrpc: "2.0", method: "history.page", params: page }));
      setTimeout(() => send(index + 1));
    };
    setTimeout(() => send(0));
    return undefined;
  }

  private details(commitId: string): CommitDetails {
    this.detailsAsked.push(commitId);
    if (commitId === sampleDetails.id) return sampleDetails;
    const commit = this.history.find((c) => c.id === commitId);
    if (!commit) throw { code: -32002, message: `No commit ${commitId}`, data: { kind: "GitFailed" } };
    return detailsOf(commit);
  }

  private file(blobId: string): HistoryFile {
    this.filesAsked.push(blobId);
    return this.files.get(blobId) ?? { kind: "Text", text: `blob ${blobId}\n`, message: null };
  }

  private respond(method: keyof Methods, params: unknown): unknown {
    const p = (params ?? {}) as { path?: string; repoId?: string; terminal?: TerminalKind; version?: string; commitId?: string };
    switch (method) {
      case "history.read":
        return undefined; // streamed by answer()
      case "history.update":
        return this.updateHistory(params as { tips: string[] } & HistoryShow);
      case "history.refs":
        return { refs: this.refs, agentHeads: this.agentHeads, agentSubjectPrefixes: this.agentSubjectPrefixes } satisfies HistoryRefs;
      case "history.details":
        return this.details(p.commitId ?? "");
      case "history.file":
        return this.file((params as { blobId: string }).blobId);
      case "app.info":
        return this.info;
      case "repo.open": {
        const state = this.repos.get((p.path ?? "").toLowerCase());
        if (!state) throw { code: -32001, message: `Not a git repository: ${p.path}`, data: { kind: "NotARepo" } };
        return this.scanOnOpen ? scanningState(state) : state;
      }
      case "repo.refresh":
        return this.state(p.repoId ?? "");
      case "system.openFolder":
        this.openedFolders.push(p.path ?? "");
        return null;
      case "system.listTerminals":
        return this.terminals;
      case "system.openTerminal":
        this.openedTerminals.push(p.terminal ? `${p.path} in ${p.terminal}` : (p.path ?? ""));
        return null;
      case "git.checkUpdate":
        return this.gitUpdate;
      case "git.update":
        this.gitUpdates.push(p.version ?? "");
        return this.gitUpdateResult;
      case "git.inUse":
        return this.gitInUse;
      case "git.openDownloadPage":
        this.downloadPageOpened++;
        return null;
      case "repo.close":
      case "app.shutdown":
        return null;
      case "changes.status":
      case "changes.file":
      case "changes.stage":
      case "changes.unstage":
      case "changes.commit":
        return this.changes(method, params);
      case "remote.fetch":
      case "remote.pull":
      case "remote.push":
        return this.remote(method, params as { repoId: string; worktree: string });
      case "ref.checkName":
      case "branch.create":
      case "branch.switch":
      case "tag.create":
      case "branch.reset":
      case "branch.undoReset":
        this.refsAsked.push({ method, params });
        return answerRefs(this, method, params as Parameters<typeof answerRefs>[2]);
    }
  }
}

/** A repo's state before its scan: worktrees and refs only, as the sidecar's quick snapshot has them. */
export function scanningState(state: RepoState): RepoState {
  return {
    ...state,
    scanning: true,
    scanProblem: null,
    unreachableCountedAt: null,
    summary: { total: 0, safeToRemove: 0, needsReview: 0, inUse: 0 },
    leftovers: [],
  };
}
