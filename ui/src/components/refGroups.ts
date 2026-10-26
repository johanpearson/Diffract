import type { HistoryRef } from "../protocol";

/** One ref as the sidebar lists it: its label within its folder, and the ref itself. */
export interface RefLeaf {
  label: string;
  ref: HistoryRef;
}

/** A level of the list: refs, and folders of refs that share a first name part, such as "feature/" or "claude/". */
export interface RefLevel {
  leaves: RefLeaf[];
  folders: { name: string; leaves: RefLeaf[] }[];
}

/** The sidebar's sections. Remote branches are listed under their remote. */
export interface SidebarRefs {
  branches: RefLevel;
  remotes: { name: string; level: RefLevel }[];
  tags: RefLevel;
}

/**
 * Sorts refs into the sidebar's sections. Each name is split at its first "/" into a folder, so agent branches such
 * as `claude/fix-1` stay out of the way under one folder. A remote's own HEAD is a pointer, not a branch, and is left
 * out. Checkpoints and stashes are History's business only.
 */
export function sidebarRefs(refs: readonly HistoryRef[]): SidebarRefs {
  const remotes = new Map<string, RefLeaf[]>();
  for (const ref of refs.filter((r) => r.kind === "RemoteBranch")) {
    const [remote, branch] = splitFirst(ref.name);
    if (branch === null || branch === "HEAD") continue;
    remotes.set(remote, [...(remotes.get(remote) ?? []), { label: branch, ref }]);
  }
  return {
    branches: levelOf(leavesOf(refs, "Branch")),
    remotes: [...remotes].sort(([a], [b]) => byName(a, b)).map(([name, leaves]) => ({ name, level: levelOf(leaves) })),
    tags: levelOf(leavesOf(refs, "Tag")),
  };
}

function leavesOf(refs: readonly HistoryRef[], kind: HistoryRef["kind"]): RefLeaf[] {
  return refs.filter((r) => r.kind === kind).map((ref) => ({ label: ref.name, ref }));
}

function levelOf(leaves: RefLeaf[]): RefLevel {
  const folders = new Map<string, RefLeaf[]>();
  const top: RefLeaf[] = [];
  for (const leaf of leaves) {
    const [folder, rest] = splitFirst(leaf.label);
    if (rest === null) top.push(leaf);
    else folders.set(folder, [...(folders.get(folder) ?? []), { label: rest, ref: leaf.ref }]);
  }
  return {
    leaves: sorted(top),
    folders: [...folders].sort(([a], [b]) => byName(a, b)).map(([name, inFolder]) => ({ name, leaves: sorted(inFolder) })),
  };
}

function splitFirst(name: string): [string, string | null] {
  const at = name.indexOf("/");
  return at < 0 ? [name, null] : [name.slice(0, at), name.slice(at + 1)];
}

function sorted(leaves: RefLeaf[]): RefLeaf[] {
  return [...leaves].sort((a, b) => byName(a.label, b.label));
}

function byName(a: string, b: string): number {
  return a.localeCompare(b, "en", { sensitivity: "base", numeric: true });
}
