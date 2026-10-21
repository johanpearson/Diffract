import openSample from "../../../tests/Diffract.Sidecar.Tests/Contract/repo.open.json";
import type { AppInfo, GitUpdateCheck, Methods, RepoChange, RepoState, TerminalInfo, TerminalKind } from "../protocol";
import { FakeTransport } from "./fakeTransport";

/** The repo in the repo.open contract sample, which the sidecar's own tests write. */
export const sampleState = (openSample as { result: RepoState }).result;

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
  /** Requests with these methods get no answer until `release` is called, like a slow sidecar. */
  readonly held = new Set<keyof Methods>();
  /** Set to make the next request to that method fail. */
  readonly failures = new Map<keyof Methods, Answer>();

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
      // The transport records a request before asking for its answer, so the last one sent is this one.
      const id = this.transport.sent.at(-1)?.id;
      this.waiting.push(() => this.transport.emit(JSON.stringify({ jsonrpc: "2.0", id, result: this.respond(method, params) })));
      return undefined;
    }
    return this.respond(method, params);
  }

  private respond(method: keyof Methods, params: unknown): unknown {
    const p = (params ?? {}) as { path?: string; repoId?: string; terminal?: TerminalKind };
    switch (method) {
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
      case "repo.close":
      case "app.shutdown":
        return null;
    }
  }
}
