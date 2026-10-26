import type { MenuItem } from "../components/ContextMenu";
import { folderName } from "../model/format";
import type { CommitEntry } from "../protocol";
import type { Marker } from "./markers";

/** What the menu acts on: the worktree the repo was opened from, as the history shows it now. */
export interface CurrentWorktree {
  path: string;
  /** Null when HEAD is detached. */
  branch: string | null;
  /** The commit checked out there, or null before the first commit. */
  head: string | null;
}

/** Where to switch: as `branch.switch` takes it. */
export type SwitchTo = { branch: string } | { remoteBranch: string } | { commit: string };

export interface MenuActions {
  copy(text: string, what: string): void;
  createBranch(commit: CommitEntry): void;
  createTag(commit: CommitEntry): void;
  switchTo(target: SwitchTo): void;
  reset(commit: CommitEntry): void;
}

export interface MenuContext {
  commit: CommitEntry;
  /** The branches, remote branches and tags on the commit. */
  markers: readonly Marker[];
  current: CurrentWorktree;
  /** Every local branch's name, so a remote branch that already has one is not offered as a new one. */
  localBranches: ReadonlySet<string>;
}

/** The items of a history row's menu, as decision 11 lists them for milestone 4. Nothing else is offered yet. */
export function commitMenuItems(context: MenuContext, actions: MenuActions): MenuItem[] {
  const { commit, current } = context;
  return [
    { label: "Copy hash", run: () => actions.copy(commit.id, "the hash") },
    { label: "Copy subject", run: () => actions.copy(commit.subject, "the subject") },
    { label: "Create branch here…", run: () => actions.createBranch(commit) },
    ...switchItems(context, actions),
    { label: "Check out this commit (detached)", disabled: current.branch === null && current.head === commit.id, run: () => actions.switchTo({ commit: commit.id }) },
    { label: "Create tag here…", run: () => actions.createTag(commit) },
    ...resetItems(context, actions),
  ];
}

/** A branch on the commit to switch to, unless it is the current one or is checked out in another worktree. */
function switchItems({ markers, current, localBranches }: MenuContext, actions: MenuActions): MenuItem[] {
  const local = markers.filter((m) => m.kind === "Branch" && m.name !== current.branch).map((m) => branchItem(m, actions));
  const remote = markers
    .filter((m) => m.kind === "RemoteBranch" && !localBranches.has(localName(m.name)))
    .map((m) => ({ label: `Switch to ${m.name} as new branch ${localName(m.name)}`, run: () => actions.switchTo({ remoteBranch: m.name }) }));
  return [...local, ...remote];
}

function branchItem(m: Marker, actions: MenuActions): MenuItem {
  if (m.worktree !== null) return { label: `Switch to ${m.name} (checked out in ${folderName(m.worktree)})`, disabled: true, run: () => {} };
  return { label: `Switch to ${m.name}`, run: () => actions.switchTo({ branch: m.name }) };
}

function resetItems({ commit, current }: MenuContext, actions: MenuActions): MenuItem[] {
  if (current.branch === null) return [];
  return [{ label: `Reset ${current.branch} to here…`, disabled: current.head === commit.id, run: () => actions.reset(commit) }];
}

/** "topic" for "origin/topic". */
export function localName(remoteBranch: string): string {
  return remoteBranch.slice(remoteBranch.indexOf("/") + 1);
}
