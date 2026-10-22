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
/** "Scanned": the scan after opening finished or failed, and this is the first full state. */
export const changeKinds = ["WorktreeFiles", "WorktreeHead", "Refs", "WorktreeList", "Everything", "Scanned"] as const;
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

/** A program using Git for Windows, from `git.inUse`. Its installer will not replace git while it runs. */
export interface GitInUse {
  /** Such as "Visual Studio Code" or "Git Bash". */
  program: string;
  /** The program that started it, when that tells more. */
  startedBy: string | null;
  /** How many of Git for Windows' processes it runs. */
  processes: number;
}

/** Result of `git.update`. */
export interface GitUpdateResult {
  outcome: GitUpdateOutcome;
  /** What happened, in plain English. */
  message: string;
  /** When programs using git stopped the update: who they are. Empty otherwise. */
  inUse: GitInUse[];
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
  /** When unreachable commits were last counted. Their rows are as old as this. Null until the first scan. */
  unreachableCountedAt: string | null;
  /**
   * The scan after opening is still running: worktree state, leftovers and unreachable commits are not known yet, and
   * arrive in a `repo.changed` with the change kind "Scanned". Refs and worktrees are known, so history can show.
   */
  scanning: boolean;
  /** Why the scan after opening failed, in plain English. A refresh tries again. */
  scanProblem: string | null;
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

export const historyRefKinds = ["Branch", "RemoteBranch", "Tag", "Checkpoint", "Stash"] as const;
export type HistoryRefKind = (typeof historyRefKinds)[number];

export const fileChangeKinds = ["Added", "Deleted", "Modified", "Renamed", "Copied", "TypeChanged", "Unknown"] as const;
export type FileChangeKind = (typeof fileChangeKinds)[number];

export const fileContentKinds = ["Text", "Binary", "TooLarge"] as const;
export type FileContentKind = (typeof fileContentKinds)[number];

/**
 * Tracked: a file git tracks or one added to the index. Conflicted: unmerged, listed but not resolved here.
 * Submodule: a commit in another repo, with no diff. NestedRepo: an untracked folder holding a repo or worktree of
 * its own, such as an agent's worktree inside this one. It has no diff and must never be staged.
 */
export const changedFileKinds = ["Tracked", "Untracked", "Conflicted", "Submodule", "NestedRepo"] as const;
export type ChangedFileKind = (typeof changedFileKinds)[number];

/** One commit as the history list shows it. */
export interface CommitEntry {
  id: string;
  /** First parent first. Empty for a root commit. */
  parentIds: string[];
  authorName: string;
  authorEmail: string;
  authoredAt: string;
  committedAt: string;
  /** The first line of the message. Can be empty. */
  subject: string;
}

/** Params of the `history.page` notification. Every page of a read arrives before its result. */
export interface HistoryPage {
  /** The token the UI passed to `history.read`. */
  token: string;
  /** 0 for the first page, then 1, 2 and so on. */
  index: number;
  /** In topological order, continuing the previous page. */
  commits: CommitEntry[];
}

/** Result of `history.read`, sent after its last page. */
export interface HistoryReadResult {
  commits: number;
  pages: number;
  /** The commits the history was read from. Pass them to `history.update` to learn what changed since. */
  tips: string[];
}

/** Which refs history starts from besides branches, tags and worktree HEADs. */
export interface HistoryShow {
  /** Agents' checkpoint refs. */
  checkpoints: boolean;
  /** Every stash. The scan after opening lists them, so before it there are none. */
  stashes: boolean;
}

/** Result of `history.update`: how the history changes to match the repo's current refs and the toggles. */
export interface HistoryUpdateResult {
  /** New commits in topological order. They all go above the commits the UI has. */
  added: CommitEntry[];
  /** Commits no ref shown reaches any more, such as those a force-move left behind. */
  removed: string[];
  /** The commits the updated history starts from, for the next `history.update`. */
  tips: string[];
  /** Too much changed to send as an update: read the whole history again. `added` and `removed` are then empty. */
  reread: boolean;
}

/** A ref history can show. Worktree HEADs are in the repo state. */
export interface HistoryRef {
  /** Short name: "feature/x", "origin/feature/x", "v1.0", "copilot/checkpoints/abc/1" or "stash@{0}". */
  name: string;
  /** Checkpoints and stashes are shown only when their toggle is on. */
  kind: HistoryRefKind;
  /** The commit it points at, after peeling annotated tags. */
  targetId: string;
  /** The agent it looks like it belongs to, from the patterns in diffract.json. A hint only. */
  agent: string | null;
}

/** Result of `history.refs`: the refs, and what tells agent work apart. */
export interface HistoryRefs {
  refs: HistoryRef[];
  /** The HEAD of every worktree that looks like it belongs to an agent, by its folder or branch. */
  agentHeads: string[];
  /** A commit whose subject starts with one of these looks like an agent made it. */
  agentSubjectPrefixes: string[];
}

/** One changed file in a commit, compared with its first parent. */
export interface FileChange {
  kind: FileChangeKind;
  path: string;
  /** The path before a rename or copy. */
  oldPath: string | null;
  /** The blob before the change, for `history.file`. Null when the file was added. */
  oldId: string | null;
  /** The blob after the change, for `history.file`. Null when the file was deleted. */
  newId: string | null;
  /** 0 to 100 for renames and copies. */
  similarity: number | null;
  /** Null for binary files. */
  additions: number | null;
  deletions: number | null;
  isBinary: boolean;
  /** A submodule commit has no content in this repo, so never ask for it. */
  isSubmodule: boolean;
}

/** Result of `history.details`. */
export interface CommitDetails {
  id: string;
  parentIds: string[];
  authorName: string;
  authorEmail: string;
  authoredAt: string;
  committerName: string;
  committerEmail: string;
  committedAt: string;
  subject: string;
  /** The message after the subject. Can be empty. */
  body: string;
  /** Changes against the first parent. */
  files: FileChange[];
}

/** Result of `history.file`: one side of a file's diff. */
export interface HistoryFile {
  kind: FileContentKind;
  /** The content when `kind` is Text. */
  text: string | null;
  /** Why there is no text, in plain English. Null for text. */
  message: string | null;
}

/** One file that differs between HEAD, the index and the worktree. */
export interface ChangedFile {
  /** Relative to the worktree, with forward slashes. */
  path: string;
  /** The path before a rename or copy. */
  oldPath: string | null;
  kind: ChangedFileKind;
  /** How the index differs from HEAD. Null when it does not. */
  staged: FileChangeKind | null;
  /** How the worktree differs from the index. Null when it does not. Added for untracked files. */
  unstaged: FileChangeKind | null;
  /** Pass to `changes.file` as they are. Null when HEAD has no such file. */
  headId: string | null;
  /** Pass to `changes.file` as they are. Null when the index has none, as for a deleted or untracked file. */
  indexId: string | null;
}

/** Result of `changes.status`: one worktree's branch and changed files. */
export interface WorktreeChanges {
  worktree: string;
  /** Null when HEAD is detached. */
  branch: string | null;
  /** Null before the first commit. */
  headCommit: string | null;
  /** Such as "origin/main". Null when the branch has none. */
  upstream: string | null;
  /** Null without an upstream, or when it is gone. */
  ahead: number | null;
  behind: number | null;
  /** In git's order: tracked files by path, then untracked ones. */
  files: ChangedFile[];
}

/** Params of `changes.file`: the file's `path`, `headId` and `indexId` from `changes.status`. */
export interface ChangesFileParams {
  repoId: string;
  worktree: string;
  path: string;
  /** HEAD against the index when true, the index against the worktree when false. */
  staged: boolean;
  headId: string | null;
  indexId: string | null;
}

/** Result of `changes.file`: both sides of a file's diff. A side is null when the file is not there. */
export interface ChangesFile {
  /** HEAD for a staged diff, the index for an unstaged one. */
  old: HistoryFile | null;
  /** The index for a staged diff, the file on disk for an unstaged one. */
  new: HistoryFile | null;
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
  /** The programs using Git for Windows right now, apart from Diffract. Only reads the process list. */
  "git.inUse": { params: undefined; result: GitInUse[] };
  /** Opens gitforwindows.org in the browser. */
  "git.openDownloadPage": { params: undefined; result: null };
  /**
   * Streams the whole history as `history.page` notifications carrying `token`, then answers with the totals and
   * the tips it was read from. One git call. Cancel the request to stop it.
   */
  "history.read": { params: { repoId: string; token: string } & HistoryShow; result: HistoryReadResult };
  /**
   * What changed since the history was read from `tips`, after the refs moved or a toggle changed. No git when
   * nothing did, one call for new commits, and one more when a branch was moved back or removed.
   */
  "history.update": { params: { repoId: string; tips: string[] } & HistoryShow; result: HistoryUpdateResult };
  /** Branches, remote branches, tags, checkpoints and stashes, from the repo's snapshot. Starts no git. */
  "history.refs": { params: { repoId: string }; result: HistoryRefs };
  /** One commit's message, committer and changed files. */
  "history.details": { params: { repoId: string; commitId: string }; result: CommitDetails };
  /** One side of a file's diff, by the `oldId` or `newId` from `history.details`. Binary and large files come without text. */
  "history.file": { params: { repoId: string; blobId: string }; result: HistoryFile };
  /** One worktree's branch, ahead and behind, and changed files, every untracked file on its own. One git call. */
  "changes.status": { params: { repoId: string; worktree: string }; result: WorktreeChanges };
  /**
   * Both sides of one file's diff. An unstaged diff has the line endings of the file on disk on both sides, so only
   * real changes show. At most one git call per side.
   */
  "changes.file": { params: ChangesFileParams; result: ChangesFile };
}

/** Every notification the sidecar sends, with its params. */
export interface Notifications {
  "repo.changed": RepoChanged;
  "history.page": HistoryPage;
}
