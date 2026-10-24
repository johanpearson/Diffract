import {
  changeKinds,
  changedFileKinds,
  remoteOutcomes,
  resetModes,
  fileChangeKinds,
  fileContentKinds,
  historyRefKinds,
  terminalKinds,
  errorKinds,
  gitUpdateOutcomes,
  gitUpdateStatuses,
  leftoverKinds,
  verdicts,
  type AppInfo,
  type ChangedFile,
  type ChangesCommitResult,
  type RemoteSyncResult,
  type RefChange,
  type ResetResult,
  type ChangesFile,
  type Hunk,
  type CommitDetails,
  type CommitEntry,
  type FileChange,
  type GitInUse,
  type GitUpdateCheck,
  type GitUpdateResult,
  type HistoryFile,
  type HistoryPage,
  type HistoryReadResult,
  type HistoryRef,
  type HistoryRefs,
  type HistoryUpdateResult,
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
  type WorktreeChanges,
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
  unreachableCountedAt: nullable(date),
  scanning: boolean,
  scanProblem: nullable(string),
  snapshot: snapshotSummary,
  summary: leftoverSummary,
  leftovers: arrayOf(leftover),
});

const gitInUse = object<GitInUse>({ program: string, startedBy: nullable(string), processes: number });

const repoChange = object<RepoChange>({ kind: oneOf(changeKinds), path: nullable(string) });

const commitEntry = object<CommitEntry>({
  id: string,
  parentIds: arrayOf(string),
  authorName: string,
  authorEmail: string,
  authoredAt: date,
  committedAt: date,
  subject: string,
});

const fileChange = object<FileChange>({
  kind: oneOf(fileChangeKinds),
  path: string,
  oldPath: nullable(string),
  oldId: nullable(string),
  newId: nullable(string),
  similarity: nullable(number),
  additions: nullable(number),
  deletions: nullable(number),
  isBinary: boolean,
  isSubmodule: boolean,
});

const commitDetails = object<CommitDetails>({
  id: string,
  parentIds: arrayOf(string),
  authorName: string,
  authorEmail: string,
  authoredAt: date,
  committerName: string,
  committerEmail: string,
  committedAt: date,
  subject: string,
  body: string,
  files: arrayOf(fileChange),
});

const historyFile = object<HistoryFile>({ kind: oneOf(fileContentKinds), text: nullable(string), message: nullable(string) });

const changedFile = object<ChangedFile>({
  path: string,
  oldPath: nullable(string),
  kind: oneOf(changedFileKinds),
  staged: nullable(oneOf(fileChangeKinds)),
  unstaged: nullable(oneOf(fileChangeKinds)),
  headId: nullable(string),
  indexId: nullable(string),
});

const worktreeChanges = object<WorktreeChanges>({
  worktree: string,
  branch: nullable(string),
  headCommit: nullable(string),
  upstream: nullable(string),
  ahead: nullable(number),
  behind: nullable(number),
  headOnRemote: nullable(string),
  files: arrayOf(changedFile),
});

const hunk = object<Hunk>({ id: string, oldStart: number, oldLines: number, newStart: number, newLines: number });

const refChange = object<RefChange>({ ref: nullable(string), message: string });

export const resetResult = object<ResetResult>({ branch: string, oldCommit: string, newCommit: string, mode: oneOf(resetModes) });

const remoteSyncResult = object<RemoteSyncResult>({ outcome: oneOf(remoteOutcomes), message: string, status: worktreeChanges });

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
  "git.update": object<GitUpdateResult>({ outcome: oneOf(gitUpdateOutcomes), message: string, inUse: arrayOf(gitInUse) }),
  "git.inUse": arrayOf(gitInUse),
  "git.openDownloadPage": nullValue,
  "history.read": object<HistoryReadResult>({ commits: number, pages: number, tips: arrayOf(string) }),
  "history.update": object<HistoryUpdateResult>({ added: arrayOf(commitEntry), removed: arrayOf(string), tips: arrayOf(string), reread: boolean }),
  "history.refs": object<HistoryRefs>({
    refs: arrayOf(object<HistoryRef>({ name: string, kind: oneOf(historyRefKinds), targetId: string, agent: nullable(string) })),
    agentHeads: arrayOf(string),
    agentSubjectPrefixes: arrayOf(string),
  }),
  "history.details": commitDetails,
  "history.file": historyFile,
  "changes.status": worktreeChanges,
  "changes.file": object<ChangesFile>({ old: nullable(historyFile), new: nullable(historyFile), hunks: arrayOf(hunk) }),
  "changes.stage": worktreeChanges,
  "changes.unstage": worktreeChanges,
  "changes.commit": object<ChangesCommitResult>({ commitId: string, status: worktreeChanges }),
  "remote.fetch": remoteSyncResult,
  "remote.pull": remoteSyncResult,
  "remote.push": remoteSyncResult,
  "ref.checkName": object<{ problem: string | null }>({ problem: nullable(string) }),
  "branch.create": refChange,
  "branch.switch": refChange,
  "tag.create": refChange,
  "branch.reset": resetResult,
  "branch.undoReset": resetResult,
};

export const notificationShapes: { [N in keyof Notifications]: Shape } = {
  "repo.changed": object<RepoChanged>({ changes: arrayOf(repoChange), state: repoState }),
  "history.page": object<HistoryPage>({ token: string, index: number, commits: arrayOf(commitEntry) }),
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
