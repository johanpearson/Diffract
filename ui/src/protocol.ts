// The sidecar protocol. Mirrors Protocol.cs and the records it uses in the sidecar.
// The contract samples in tests/Diffract.Sidecar.Tests/Contract keep the two sides in step.
// Dates are ISO 8601 strings with an offset, as the sidecar writes them.

export const verdicts = ["SafeToRemove", "NeedsReview", "InUse"] as const;
export type Verdict = (typeof verdicts)[number];

export const leftoverKinds = [
  "Worktree",
  "PrunableWorktree",
  "LocalBranch",
  "RemoteBranch",
  "Stash",
  "CheckpointSession",
  "OrphanedCommit",
  "BackupTag",
  "LeftoverFolder",
] as const;
export type LeftoverKind = (typeof leftoverKinds)[number];

/** What went wrong, on Diffract's own errors. */
export const errorKinds = ["NotARepo", "GitTooOld", "GitFailed", "TimedOut", "ConfigInvalid"] as const;
export type ErrorKind = (typeof errorKinds)[number];

/** What a live change refreshed. */
export const changeKinds = ["WorktreeFiles", "WorktreeHead", "Refs", "WorktreeList", "Everything"] as const;
export type ChangeKind = (typeof changeKinds)[number];

/** Result of `app.info`. */
export interface AppInfo {
  sidecarVersion: string;
  /** Null when git could not be run. */
  gitVersion: string | null;
  gitSupported: boolean;
  /** Why Diffract cannot use this git, in plain English. Null when it can. */
  gitProblem: string | null;
}

export interface WorktreeInfo {
  path: string;
  isMain: boolean;
  head: string | null;
  /** Null when detached or bare. */
  branch: string | null;
  isBare: boolean;
  isDetached: boolean;
  isLocked: boolean;
  lockReason: string | null;
  isPrunable: boolean;
  prunableReason: string | null;
  /** Null when status could not run (bare or prunable). */
  dirtyFileCount: number | null;
  aheadOfMain: number | null;
  behindMain: number | null;
  lastCommitAt: string | null;
  lastActivityAt: string | null;
}

/** A folder that looks like what is left of a worktree, but that git does not know. */
export interface LeftoverFolderInfo {
  path: string;
  holdsNoFiles: boolean;
  wasWorktree: boolean;
}

export interface SnapshotSummary {
  worktrees: WorktreeInfo[];
  localBranches: number;
  remoteBranches: number;
  tags: number;
  stashes: number;
  otherRefs: number;
  unreachableCommits: number;
  leftoverFolders: LeftoverFolderInfo[];
}

export interface LeftoverSummary {
  total: number;
  safeToRemove: number;
  needsReview: number;
  inUse: number;
}

export interface Leftover {
  kind: LeftoverKind;
  name: string;
  path: string | null;
  /** A hint from config patterns. Never used to decide anything. */
  agent: string | null;
  verdict: Verdict;
  commitsAtRisk: number;
  /** One plain-English sentence. */
  reason: string;
}

/** Result of `repo.open` and `repo.refresh`, and the new state in `repo.changed`. */
export interface RepoState {
  repoId: string;
  /** The main worktree, whatever path the repo was opened with. */
  repoRoot: string;
  mainBranch: string;
  takenAt: string;
  /** When unreachable commits were last counted. Their rows are as old as this. */
  unreachableCountedAt: string;
  snapshot: SnapshotSummary;
  summary: LeftoverSummary;
  leftovers: Leftover[];
}

export interface RepoChange {
  kind: ChangeKind;
  /** The worktree, for changes that belong to one. */
  path: string | null;
}

/** Params of the `repo.changed` notification. Replace the repo's state with `state`. */
export interface RepoChanged {
  changes: RepoChange[];
  state: RepoState;
}

/** Every method the sidecar answers, with its params and result. */
export interface Methods {
  "app.info": { params: undefined; result: AppInfo };
  "repo.open": { params: { path: string }; result: RepoState };
  /** A full rebuild. `includeUnreachable` also runs `git fsck`, the slowest call by far. */
  "repo.refresh": { params: { repoId: string; includeUnreachable: boolean }; result: RepoState };
  "repo.close": { params: { repoId: string }; result: null };
  "app.shutdown": { params: undefined; result: null };
}

/** Every notification the sidecar sends, with its params. */
export interface Notifications {
  "repo.changed": RepoChanged;
}
