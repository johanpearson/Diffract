import type { HistoryRef } from "../protocol";

/** The stashes among the refs, newest first, as git lists them. */
export function stashesOf(refs: readonly HistoryRef[]): HistoryRef[] {
  return refs.filter((r) => r.kind === "Stash").sort((a, b) => indexOf(a) - indexOf(b));
}

/**
 * What the sidebar shows for a stash: its message without the "On main: " or "WIP on main: " git puts first, which the
 * tooltip keeps. A stash with no message shows its place, "stash@{0}".
 */
export function stashLabel(ref: HistoryRef): string {
  const message = ref.message ?? "";
  const label = message.replace(/^(WIP on|On) [^:]*: /, "");
  return label.length > 0 ? label : ref.name;
}

function indexOf(ref: HistoryRef): number {
  return Number(/^stash@\{(\d+)\}$/.exec(ref.name)?.[1] ?? Number.MAX_SAFE_INTEGER);
}
