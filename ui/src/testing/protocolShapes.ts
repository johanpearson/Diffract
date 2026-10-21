import {
  changeKinds,
  terminalKinds,
  errorKinds,
  gitUpdateOutcomes,
  gitUpdateStatuses,
  leftoverKinds,
  verdicts,
  type AppInfo,
  type GitUpdateCheck,
  type GitUpdateResult,
  type Leftover,
  type LeftoverFolderInfo,
  type LeftoverSummary,
  type Methods,
  type Notifications,
  type RepoChange,
  type RepoChanged,
  type RepoState,
  type SnapshotSummary,
  type TerminalInfo,
  type WorktreeInfo,
} from "../protocol";
import { arrayOf, boolean, date, nullable, nullValue, number, object, oneOf, optional, string, type Shape } from "./shapes";

const appInfo = object<AppInfo>({ sidecarVersion: string, gitVersion: nullable(string), gitSupported: boolean, gitProblem: nullable(string) });

const worktree = object<WorktreeInfo>({
  path: string,
  isMain: boolean,
  head: nullable(string),
  branch: nullable(string),
  isBare: boolean,
  isDetached: boolean,
  isLocked: boolean,
  lockReason: nullable(string),
  isPrunable: boolean,
  prunableReason: nullable(string),
  dirtyFileCount: nullable(number),
  aheadOfMain: nullable(number),
  behindMain: nullable(number),
  lastCommitAt: nullable(date),
  lastActivityAt: nullable(date),
});

const leftoverFolder = object<LeftoverFolderInfo>({ path: string, holdsNoFiles: boolean, wasWorktree: boolean });

const snapshotSummary = object<SnapshotSummary>({
  worktrees: arrayOf(worktree),
  localBranches: number,
  remoteBranches: number,
  tags: number,
  stashes: number,
  otherRefs: number,
  unreachableCommits: number,
  leftoverFolders: arrayOf(leftoverFolder),
});

const leftoverSummary = object<LeftoverSummary>({ total: number, safeToRemove: number, needsReview: number, inUse: number });

const leftover = object<Leftover>({
  kind: oneOf(leftoverKinds),
  name: string,
  path: nullable(string),
  agent: nullable(string),
  verdict: oneOf(verdicts),
  commitsAtRisk: nullable(number),
  reason: string,
});

const repoState = object<RepoState>({
  repoId: string,
  repoRoot: string,
  mainBranch: string,
  takenAt: date,
  unreachableCountedAt: date,
  snapshot: snapshotSummary,
  summary: leftoverSummary,
  leftovers: arrayOf(leftover),
});

const repoChange = object<RepoChange>({ kind: oneOf(changeKinds), path: nullable(string) });

/** The result of every method. Adding a method to `Methods` without a shape here fails the type check. */
export const resultShapes: { [M in keyof Methods]: Shape } = {
  "app.info": appInfo,
  "repo.open": repoState,
  "repo.refresh": repoState,
  "repo.close": nullValue,
  "app.shutdown": nullValue,
  "system.openFolder": nullValue,
  "system.listTerminals": arrayOf(object<TerminalInfo>({ id: oneOf(terminalKinds), name: string })),
  "system.openTerminal": nullValue,
  "git.checkUpdate": object<GitUpdateCheck>({
    status: oneOf(gitUpdateStatuses),
    installedVersion: nullable(string),
    availableVersion: nullable(string),
    problem: nullable(string),
    downloadPage: string,
  }),
  "git.update": object<GitUpdateResult>({ outcome: oneOf(gitUpdateOutcomes), message: string }),
  "git.openDownloadPage": nullValue,
};

export const notificationShapes: { [N in keyof Notifications]: Shape } = {
  "repo.changed": object<RepoChanged>({ changes: arrayOf(repoChange), state: repoState }),
};

export const responseShape = (result: Shape) => object<{ jsonrpc: string; id: number; result: unknown }>({ jsonrpc: string, id: number, result });

export const notificationShape = (method: string, params: Shape) =>
  object<{ jsonrpc: string; method: string; params: unknown }>({ jsonrpc: string, method: oneOf([method]), params });

export const errorShape = object<{ jsonrpc: string; id: number; error: unknown }>({
  jsonrpc: string,
  id: number,
  error: object<{ code: number; message: string; data: unknown }>({
    code: number,
    message: string,
    data: optional(object<{ kind: unknown }>({ kind: oneOf(errorKinds) })),
  }),
});
