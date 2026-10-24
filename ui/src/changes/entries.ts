import type { ChangedFile, FileChangeKind, WorktreeChanges, WorktreeInfo } from "../protocol";

/** Which list a file is in: what is not staged yet, and what the next commit holds. */
export type Side = "unstaged" | "staged";

/** One row of a list. A file that is partly staged has a row in each. */
export interface Entry {
  /** Unique across both lists. */
  key: string;
  side: Side;
  file: ChangedFile;
  /** How this side differs. Null only for a conflicted file. */
  change: FileChangeKind | null;
  /** Why it cannot be staged or unstaged here, or null when it can. */
  blocked: string | null;
}

export interface Lists {
  unstaged: Entry[];
  staged: Entry[];
}

const blockedReasons: Partial<Record<ChangedFile["kind"], string>> = {
  Conflicted: "This file has a merge conflict. Resolving conflicts comes in a later version of Diffract.",
  Submodule: "This is a submodule. Diffract cannot stage submodules yet.",
  NestedRepo: "This folder holds a repo of its own. Diffract never stages one, as git would add it as a submodule.",
};

/**
 * The Unstaged and Staged lists, in git's order. A folder that is one of this repo's own worktrees, such as an
 * agent's under .claude/worktrees, is left out: it is not a change, just where that worktree lives.
 */
export function listsOf(changes: WorktreeChanges, worktrees: readonly WorktreeInfo[]): Lists {
  const own = new Set(worktrees.map((w) => normalize(w.path)));
  const files = changes.files.filter((f) => f.kind !== "NestedRepo" || !own.has(normalize(join(changes.worktree, f.path))));
  return {
    unstaged: files.filter((f) => f.unstaged !== null || f.kind === "Conflicted").map((f) => entry(f, "unstaged")),
    staged: files.filter((f) => f.staged !== null).map((f) => entry(f, "staged")),
  };
}

function entry(file: ChangedFile, side: Side): Entry {
  return { key: `${side}:${file.path}`, side, file, change: side === "staged" ? file.staged : file.unstaged, blocked: blockedReasons[file.kind] ?? null };
}

/** The paths git needs to stage or unstage these rows. Unstaging a rename needs both its paths. */
export function pathsOf(entries: readonly Entry[]): string[] {
  const paths = entries.flatMap((e) => (e.side === "staged" && e.file.oldPath !== null ? [e.file.path, e.file.oldPath] : [e.file.path]));
  return [...new Set(paths)];
}

const letters: Record<FileChangeKind, string> = { Added: "A", Deleted: "D", Modified: "M", Renamed: "R", Copied: "C", TypeChanged: "T", Unknown: "?" };

/** The letter shown before the path: A, M, D and so on, U for a conflict. */
export function letterOf(entry: Entry): string {
  return entry.change === null ? "U" : letters[entry.change];
}

/** What the letter stands for, for its tooltip and for screen readers. */
export function changeName(entry: Entry): string {
  if (entry.change === null) return "Conflicted";
  if (entry.file.kind === "Untracked") return "New, not tracked yet";
  return entry.change === "TypeChanged" ? "Type changed" : entry.change;
}

/** Windows paths compare without case and with either slash. */
function normalize(path: string): string {
  return path.replaceAll("\\", "/").replace(/\/+$/, "").toLowerCase();
}

function join(folder: string, path: string): string {
  return `${folder.replace(/[\\/]+$/, "")}/${path}`;
}
