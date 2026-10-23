import { describe, expect, it } from "vitest";
import type { ChangedFile, WorktreeChanges, WorktreeInfo } from "../protocol";
import { changeName, letterOf, listsOf, pathsOf } from "./entries";

const tracked = (path: string, staged: ChangedFile["staged"], unstaged: ChangedFile["unstaged"], extra: Partial<ChangedFile> = {}): ChangedFile => ({
  path,
  oldPath: null,
  kind: "Tracked",
  staged,
  unstaged,
  headId: null,
  indexId: null,
  ...extra,
});

function changes(files: ChangedFile[]): WorktreeChanges {
  return { worktree: "C:\\src\\shop", branch: "main", headCommit: null, upstream: null, ahead: null, behind: null, headOnRemote: null, files };
}

const worktree = (path: string) => ({ path }) as WorktreeInfo;

describe("the Unstaged and Staged lists", () => {
  it("puts a partly staged file in both lists, and every other file in one", () => {
    const lists = listsOf(changes([tracked("both.cs", "Modified", "Modified"), tracked("staged.cs", "Added", null), tracked("unstaged.cs", null, "Deleted")]), []);

    expect(lists.unstaged.map((e) => [e.file.path, letterOf(e)])).toEqual([
      ["both.cs", "M"],
      ["unstaged.cs", "D"],
    ]);
    expect(lists.staged.map((e) => [e.file.path, letterOf(e)])).toEqual([
      ["both.cs", "M"],
      ["staged.cs", "A"],
    ]);
    expect(new Set([...lists.unstaged, ...lists.staged].map((e) => e.key)).size).toBe(4);
  });

  it("lists a conflicted file as unstaged, with U and a reason it cannot be staged", () => {
    const { unstaged, staged } = listsOf(changes([tracked("merge.cs", null, null, { kind: "Conflicted" })]), []);

    const [entry] = unstaged;
    expect([letterOf(entry!), changeName(entry!)]).toEqual(["U", "Conflicted"]);
    expect(entry!.blocked).toMatch(/merge conflict/);
    expect(staged).toEqual([]);
  });

  it("hides a folder that is one of this repo's own worktrees, whatever its case and slashes", () => {
    const files = [
      tracked(".claude/worktrees/task", null, "Added", { kind: "NestedRepo" }),
      tracked("vendor/other-repo", null, "Added", { kind: "NestedRepo" }),
    ];

    const { unstaged } = listsOf(changes(files), [worktree("C:\\src\\shop"), worktree("c:\\SRC\\shop\\.claude\\worktrees\\task\\")]);

    expect(unstaged.map((e) => e.file.path)).toEqual(["vendor/other-repo"]);
    expect(unstaged[0]!.blocked).toMatch(/never stages/);
  });

  it("can stage tracked and untracked files, and not submodules yet", () => {
    const { unstaged } = listsOf(
      changes([tracked("new.md", null, "Added", { kind: "Untracked" }), tracked("lib", null, "Modified", { kind: "Submodule" }), tracked("a.cs", null, "Modified")]),
      [],
    );

    expect(unstaged.map((e) => e.blocked === null)).toEqual([true, false, true]);
    expect(changeName(unstaged[0]!)).toBe("New, not tracked yet");
  });

  it("unstages a rename by both its paths, and stages it by its new one", () => {
    const renamed = tracked("new name.cs", "Renamed", "Modified", { oldPath: "old name.cs" });
    const { unstaged, staged } = listsOf(changes([renamed, tracked("a.cs", "Modified", null)]), []);

    expect(pathsOf(staged)).toEqual(["new name.cs", "old name.cs", "a.cs"]);
    expect(pathsOf(unstaged)).toEqual(["new name.cs"]);
  });
});
