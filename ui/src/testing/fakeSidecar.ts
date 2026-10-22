import detailsSample from "../../../tests/Diffract.Sidecar.Tests/Contract/history.details.json";
import pageSample from "../../../tests/Diffract.Sidecar.Tests/Contract/history.page.json";
import refsSample from "../../../tests/Diffract.Sidecar.Tests/Contract/history.refs.json";
import openSample from "../../../tests/Diffract.Sidecar.Tests/Contract/repo.open.json";
import type {
  AppInfo,
  CommitDetails,
  CommitEntry,
  GitInUse,
  GitUpdateCheck,
  GitUpdateResult,
  HistoryFile,
  HistoryPage,
  HistoryRef,
  Methods,
  RepoChange,
  RepoState,
  TerminalInfo,
  TerminalKind,
} from "../protocol";
import { detailsOf, generateHistory, sampleFiles } from "./fakeHistory";
import { FakeTransport } from "./fakeTransport";

/** The repo in the repo.open contract sample, which the sidecar's own tests write. */
export const sampleState = (openSample as { result: RepoState }).result;

/** The history in the contract samples: a merge whose second parent is not in it, and a root commit. */
export const sampleCommits = (pageSample as { params: HistoryPage }).params.commits;
export const sampleRefs = (refsSample as { result: HistoryRef[] }).result;
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
  /** What history.read streams, newest first. */
  history: CommitEntry[] = sampleCommits;
  /** What history.refs answers. */
  refs: HistoryRef[] = sampleRefs;
  /** Every commit history.details was asked for. */
  readonly detailsAsked: string[] = [];
  /** What history.file answers, by blob id. Any other blob is a one-line text naming it. */
  readonly files = new Map<string, HistoryFile>(sampleFiles);
  /** Every blob history.file was asked for. */
  readonly filesAsked: string[] = [];

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

  /** Replaces the history with a generated one of `count` commits, with its branches and a tag. */
  useGeneratedHistory(count: number): void {
    ({ commits: this.history, refs: this.refs } = generateHistory(count));
  }

  release(): void {
    this.held.clear();
    for (const answer of this.waiting.splice(0)) answer();
  }

  private answer(method: keyof Methods, params: unknown): unknown {
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
    if (method === "history.read") return this.streamHistory((params as { token: string }).token);
    return this.respond(method, params);
  }

  // The transport records a request before asking for its answer, so the last one sent is this one.
  private lastRequestId(): number | undefined {
    return this.transport.sent.at(-1)?.id;
  }

  /** Sends the history page by page, each in its own task as from a real pipe, then the totals. */
  private streamHistory(token: string): undefined {
    const id = this.lastRequestId();
    const commits = this.history;
    const pages = Math.ceil(commits.length / HistoryPageSize);
    const send = (index: number) => {
      if (index === pages) {
        this.transport.emit(JSON.stringify({ jsonrpc: "2.0", id, result: { commits: commits.length, pages } }));
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
      case "history.refs":
        return this.refs;
      case "history.details":
        return this.details(p.commitId ?? "");
      case "history.file":
        return this.file((params as { blobId: string }).blobId);
      case "app.info":
        return this.info;
      case "repo.open": {
        const state = this.repos.get((p.path ?? "").toLowerCase());
        if (!state) throw { code: -32001, message: `Not a git repository: ${p.path}`, data: { kind: "NotARepo" } };
        return state;
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
    }
  }
}
