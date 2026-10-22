import { describe, expect, it } from "vitest";
import type { HistoryRef, WorktreeInfo } from "../protocol";
import { sampleState } from "../testing/fakeSidecar";
import { markersByCommit } from "./markers";

const [main, agent] = sampleState.snapshot.worktrees as [WorktreeInfo, WorktreeInfo];

const refs: HistoryRef[] = [
  { name: "stash@{0}", kind: "Stash", targetId: "a", agent: null },
  { name: "copilot/checkpoints/s/1", kind: "Checkpoint", targetId: "a", agent: "GitHub Copilot" },
  { name: "v1.0", kind: "Tag", targetId: "a", agent: null },
  { name: "origin/main", kind: "RemoteBranch", targetId: "a", agent: null },
  { name: "main", kind: "Branch", targetId: "a", agent: null },
  { name: "claude/räksmörgås-fix", kind: "Branch", targetId: "b", agent: "Claude Code" },
  { name: "old-task", kind: "Branch", targetId: "a", agent: null },
];

describe("markersByCommit", () => {
  it("puts the current branch first, then branches, remote branches, tags, checkpoints and stashes", () => {
    const markers = markersByCommit(refs, [main, agent]);

    expect(markers.get("a")!.map((m) => m.name)).toEqual(["main", "old-task", "origin/main", "v1.0", "copilot/checkpoints/s/1", "stash@{0}"]);
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
