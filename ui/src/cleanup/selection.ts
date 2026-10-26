import type { Leftover, SelectedLeftover } from "../protocol";
import type { Row } from "../model/rows";

/** "In use" is never offered, so those rows have no checkbox. Nor has a row that is fading out because it is gone. */
export function selectable(row: Row): boolean {
  return row.leftover.verdict !== "InUse" && row.flash !== "removed";
}

/**
 * A local branch that is merged, or whose upstream is gone. The sidecar's merged-branch rule is the only one that
 * finds a local branch safe to remove, and it finds exactly these, so the verdict says it.
 */
export function isMergedBranch(leftover: Leftover): boolean {
  return leftover.kind === "LocalBranch" && leftover.verdict === "SafeToRemove";
}

/** The selection with every merged branch added, and nothing else. */
export function withMergedBranches(rows: Row[], selected: ReadonlySet<string>): Set<string> {
  return new Set([...selected, ...rows.filter((row) => selectable(row) && isMergedBranch(row.leftover)).map((row) => row.key)]);
}

/**
 * The rows still selected, in list order. A row that went away, or became "In use", while it was ticked drops out,
 * so it is never part of a cleanup.
 */
export function selectedRows(rows: Row[], selected: ReadonlySet<string>): Row[] {
  return rows.filter((row) => selected.has(row.key) && selectable(row));
}

/** What `cleanup.plan` needs to find the leftover again: its kind, name and path. */
export function toSelected(leftover: Leftover): SelectedLeftover {
  return { kind: leftover.kind, name: leftover.name, path: leftover.path };
}
