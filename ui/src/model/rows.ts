import type { Leftover } from "../protocol";

/** How a row changed in the last update, so the view can highlight it for a moment. */
export type Flash = "added" | "changed" | "removed";

export interface Row {
  key: string;
  leftover: Leftover;
  flash: Flash | null;
}

/** How long a changed row stays highlighted, and a removed one stays visible, in milliseconds. */
export const FlashMs = 1500;

/**
 * Identifies an item across updates the same way the sidecar's classifier does: kind, path (or name) and agent hint.
 * Name alone is not enough: two agents can each have a "1 commit" row.
 */
export function leftoverKey(leftover: Leftover): string {
  return [leftover.kind, leftover.path ?? leftover.name, leftover.agent ?? ""].join("\u0000");
}

export function initialRows(leftovers: Leftover[]): Row[] {
  return leftovers.map((leftover) => ({ key: leftoverKey(leftover), leftover, flash: null }));
}

/**
 * The rows after an update: new items are "added", items whose verdict changed are "changed", and items that went
 * away stay where they were as "removed" until `settleRows`. Rows that are still highlighted keep their highlight.
 */
export function updateRows(previous: Row[], next: Leftover[]): Row[] {
  const before = new Map(previous.map((row) => [row.key, row]));
  const rows: Row[] = next.map((leftover) => {
    const key = leftoverKey(leftover);
    const old = before.get(key);
    const flash: Flash | null =
      old === undefined || old.flash === "removed" ? "added" : old.leftover.verdict !== leftover.verdict ? "changed" : old.flash;
    return { key, leftover, flash };
  });

  const present = new Set(rows.map((row) => row.key));
  previous.forEach((row, index) => {
    if (!present.has(row.key)) rows.splice(Math.min(index, rows.length), 0, { ...row, flash: "removed" });
  });
  return rows;
}

/** The rows once highlights have run their course: removed ones gone, the rest plain. */
export function settleRows(rows: Row[]): Row[] {
  return rows.filter((row) => row.flash !== "removed").map((row) => (row.flash === null ? row : { ...row, flash: null }));
}
