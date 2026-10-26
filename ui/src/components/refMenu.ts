import { localName, type SwitchTo } from "../history/commitMenu";
import type { HistoryRef } from "../protocol";
import type { MenuItem } from "./ContextMenu";

/** What a ref in the sidebar can do. */
export interface RefCommands {
  reveal(commitId: string): void;
  switchTo(target: SwitchTo, commitId: string): void;
  copy(text: string, what: string): void;
}

/** What the sidebar knows about the branches, to say what switching does. */
export interface BranchContext {
  /** The branch checked out where the repo was opened, or null when HEAD is detached. */
  current: string | null;
  localBranches: ReadonlySet<string>;
}

/**
 * What double-clicking a ref switches to. A branch is switched to. A remote branch becomes its local branch, which
 * is created to track it unless it is there already. A tag is only shown: checking one out leaves HEAD detached,
 * which should never happen by accident.
 */
export function switchTarget(ref: HistoryRef, context: BranchContext): SwitchTo | null {
  if (ref.kind === "Branch") return ref.name === context.current ? null : { branch: ref.name };
  if (ref.kind !== "RemoteBranch") return null;
  const local = localName(ref.name);
  if (local === context.current) return null;
  return context.localBranches.has(local) ? { branch: local } : { remoteBranch: ref.name };
}

/** The right-click menu of a ref in the sidebar. */
export function refMenu(ref: HistoryRef, context: BranchContext, commands: RefCommands): MenuItem[] {
  return [
    { label: "Show in History", run: () => commands.reveal(ref.targetId) },
    switchItem(ref, context, commands),
    { label: "Copy name", run: () => commands.copy(ref.name, "the name") },
  ];
}

function switchItem(ref: HistoryRef, context: BranchContext, commands: RefCommands): MenuItem {
  if (ref.kind === "Tag") return { label: `Check out ${ref.name} (detached)`, run: () => commands.switchTo({ commit: ref.targetId }, ref.targetId) };
  const target = switchTarget(ref, context);
  const run = () => target && commands.switchTo(target, ref.targetId);
  if (target === null) return { label: `On ${ref.kind === "Branch" ? ref.name : localName(ref.name)} already`, disabled: true, run };
  return { label: "branch" in target ? `Switch to ${target.branch}` : `Switch to ${ref.name} as new branch ${localName(ref.name)}`, run };
}
