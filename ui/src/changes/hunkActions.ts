import type { ChangeAction } from "../history/monacoEditor";
import type { Hunk } from "../protocol";
import type { Side } from "./entries";

/**
 * A Stage hunk or Unstage hunk button above each change, and a Discard hunk one beside it for a change that is not
 * staged, on the right-hand side of the diff. A change that only
 * removes lines sits after line `newStart` there, so its button goes above the line that follows.
 */
export function hunkActions(hunks: readonly Hunk[], side: Side, run: (hunk: Hunk) => void, discard?: (hunk: Hunk) => void): ChangeAction[] {
  const verb = verbOf(side);
  return hunks.flatMap((hunk) => {
    const line = lineOf(hunk);
    const toggle = { line, label: `${verb} hunk`, title: `${verb} the change at line ${line}`, run: () => run(hunk) };
    // Discarding throws the change away, so it is only offered for a change that is not staged, and asks first.
    return side === "unstaged" && discard
      ? [toggle, { line, label: "Discard hunk", title: `Discard the change at line ${line}`, run: () => discard(hunk) }]
      : [toggle];
  });
}

/** The line of the right-hand side a change is shown at. */
export function lineOf(hunk: Hunk): number {
  return hunk.newLines === 0 ? hunk.newStart + 1 : Math.max(hunk.newStart, 1);
}

export function verbOf(side: Side): "Stage" | "Unstage" {
  return side === "unstaged" ? "Stage" : "Unstage";
}
