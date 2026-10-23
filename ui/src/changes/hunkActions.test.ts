import { describe, expect, it, vi } from "vitest";
import type { Hunk } from "../protocol";
import { hunkActions } from "./hunkActions";

const hunk = (id: string, newStart: number, newLines: number): Hunk => ({ id, oldStart: newStart, oldLines: 1, newStart, newLines });

describe("a button above each change", () => {
  it("goes above the first line a change adds or changes, and says what it does", () => {
    const actions = hunkActions([hunk("a", 12, 3)], "unstaged", () => {});

    expect(actions.map(({ line, label, title }) => ({ line, label, title }))).toEqual([{ line: 12, label: "Stage hunk", title: "Stage the change at line 12" }]);
  });

  it("goes above the line after a change that only removes lines, and at the top for one before the first line", () => {
    const actions = hunkActions([hunk("removes", 7, 0), hunk("removes first", 0, 0)], "staged", () => {});

    expect(actions.map((a) => [a.line, a.label])).toEqual([
      [8, "Unstage hunk"],
      [1, "Unstage hunk"],
    ]);
  });

  it("runs with its own hunk", () => {
    const run = vi.fn();
    const hunks = [hunk("first", 2, 1), hunk("second", 9, 1)];

    hunkActions(hunks, "unstaged", run)[1]!.run();

    expect(run).toHaveBeenCalledWith(hunks[1]);
  });
});
