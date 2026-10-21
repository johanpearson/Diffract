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

/** The terminals Diffract can open, in the order the sidecar lists them. */
export const terminalKinds = ["WindowsTerminal", "WindowsPowerShell", "PowerShell7", "CommandPrompt", "GitBash"] as const;
export type TerminalKind = (typeof terminalKinds)[number];

/** An installed terminal, from `system.listTerminals`. */
export interface TerminalInfo {
  id: TerminalKind;
  name: string;
}

/** Whether a newer Git for Windows can be installed through winget. */
export const gitUpdateStatuses = ["UpToDate", "UpdateAvailable", "Unknown"] as const;
export type GitUpdateStatus = (typeof gitUpdateStatuses)[number];

/** Result of `git.checkUpdate`. */
export interface GitUpdateCheck {
  /** UpdateAvailable only when winget can install the update over the git Diffract uses. */
  status: GitUpdateStatus;
  /** The git Diffract uses, for example "2.47.1". */
  installedVersion: string | null;
  /** winget's newest Git for Windows, for example "2.55.0.5". */
  availableVersion: string | null;
  /** Why Diffract cannot tell or cannot update, in plain English. */
  problem: string | null;
  /** Where to update by hand when winget cannot. */
  downloadPage: string;
}

/** How `git.update` ended. */
export const gitUpdateOutcomes = ["Updated", "RestartNeeded", "Failed"] as const;
export type GitUpdateOutcome = (typeof gitUpdateOutcomes)[number];

/** Result of `git.update`. */
export interface GitUpdateResult {
  outcome: GitUpdateOutcome;
  /** What happened, in plain English. */
  message: string;
}

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
  /** Null when they could not be counted, or when the item holds no commits. Shown as "-". */
  commitsAtRisk: number | null;
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
  /** Opens an existing folder in Explorer. */
  "system.openFolder": { params: { path: string }; result: null };
  /** The installed terminals, the default first. */
  "system.listTerminals": { params: undefined; result: TerminalInfo[] };
  /** Opens a terminal in an existing folder. Without `terminal`, Windows Terminal, or PowerShell when it is not installed. */
  "system.openTerminal": { params: { path: string; terminal?: TerminalKind }; result: null };
  /** Asks winget whether a newer Git for Windows is available. Installs nothing. Can take several seconds. */
  "git.checkUpdate": { params: undefined; result: GitUpdateCheck };
  /**
   * Updates Git for Windows to `version`, the availableVersion the user confirmed. Checks again first and installs
   * nothing if winget now offers something else. Takes minutes, and Windows asks for permission.
   */
  "git.update": { params: { version: string }; result: GitUpdateResult };
  /** Opens gitforwindows.org in the browser. */
  "git.openDownloadPage": { params: undefined; result: null };
}

/** Every notification the sidecar sends, with its params. */
export interface Notifications {
  "repo.changed": RepoChanged;
}
