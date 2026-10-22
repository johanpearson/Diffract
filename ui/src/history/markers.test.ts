import { describe, expect, it } from "vitest";
import type { HistoryRef, WorktreeInfo } from "../protocol";
import { sampleState } from "../testing/fakeSidecar";
import { markersByCommit } from "./markers";

const [main, agent] = sampleState.snapshot.worktrees as [WorktreeInfo, WorktreeInfo];

const refs: HistoryRef[] = [
  { name: "v1.0", kind: "Tag", targetId: "a" },
  { name: "origin/main", kind: "RemoteBranch", targetId: "a" },
  { name: "main", kind: "Branch", targetId: "a" },
  { name: "claude/räksmörgås-fix", kind: "Branch", targetId: "b" },
  { name: "old-task", kind: "Branch", targetId: "a" },
];

describe("markersByCommit", () => {
  it("puts the current branch first, then branches, remote branches and tags", () => {
    const markers = markersByCommit(refs, [main, agent]);

    expect(markers.get("a")!.map((m) => m.name)).toEqual(["main", "old-task", "origin/main", "v1.0"]);
    expect(markers.get("a")![0]).toMatchObject({ isCurrent: true, worktree: main.path });
  });

  it("marks a branch checked out in another worktree with that worktree", () => {
    const markers = markersByCommit(refs, [main, agent]);

    expect(markers.get("b")).toEqual([{ name: "claude/räksmörgås-fix", kind: "Branch", isCurrent: false, worktree: agent.path }]);
  });

  it("gives a worktree on a detached HEAD its own marker", () => {
    const detached: WorktreeInfo = { ...agent, branch: null, isDetached: true, head: "c" };

    const markers = markersByCommit([], [detached]);

    expect(markers.get("c")).toEqual([{ name: "HEAD (räksmörgås fix)", kind: "Head", isCurrent: false, worktree: detached.path }]);
  });

  it("has nothing for commits no ref points at", () => {
    expect(markersByCommit(refs, []).get("z")).toBeUndefined();
  });
});
