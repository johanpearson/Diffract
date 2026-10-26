import { describe, expect, it } from "vitest";
import type { HistoryRef } from "../protocol";
import { sidebarRefs, type RefLevel } from "./refGroups";

const ref = (name: string, kind: HistoryRef["kind"]): HistoryRef => ({ name, kind, targetId: `${name}-id`, agent: null });

/** A level as plain names, so expectations read like the sidebar. */
const names = (level: RefLevel) => ({
  leaves: level.leaves.map((l) => l.label),
  folders: level.folders.map((f) => [f.name, f.leaves.map((l) => l.label)]),
});

describe("sidebarRefs", () => {
  it("sorts branches, remote branches and tags into their sections", () => {
    const refs = sidebarRefs([ref("main", "Branch"), ref("origin/main", "RemoteBranch"), ref("v1.0", "Tag")]);

    expect(names(refs.branches)).toEqual({ leaves: ["main"], folders: [] });
    expect(refs.remotes.map((r) => [r.name, names(r.level)])).toEqual([["origin", { leaves: ["main"], folders: [] }]]);
    expect(names(refs.tags)).toEqual({ leaves: ["v1.0"], folders: [] });
  });

  it("puts branches that share a first name part in a folder, and keeps the full ref", () => {
    const refs = sidebarRefs([ref("main", "Branch"), ref("claude/fix-2", "Branch"), ref("claude/fix-10", "Branch"), ref("feature/räksmörgås/ui", "Branch")]);

    expect(names(refs.branches)).toEqual({ leaves: ["main"], folders: [["claude", ["fix-2", "fix-10"]], ["feature", ["räksmörgås/ui"]]] });
    expect(refs.branches.folders[0]!.leaves[0]!.ref.name).toBe("claude/fix-2");
  });

  it("lists each remote on its own, with folders inside it, and leaves out the remote's HEAD", () => {
    const refs = sidebarRefs([ref("upstream/main", "RemoteBranch"), ref("origin/HEAD", "RemoteBranch"), ref("origin/main", "RemoteBranch"), ref("origin/copilot/task", "RemoteBranch")]);

    expect(refs.remotes.map((r) => [r.name, names(r.level)])).toEqual([
      ["origin", { leaves: ["main"], folders: [["copilot", ["task"]]] }],
      ["upstream", { leaves: ["main"], folders: [] }],
    ]);
  });

  it("leaves checkpoints and stashes to History", () => {
    const refs = sidebarRefs([ref("copilot/checkpoints/abc/1", "Checkpoint"), ref("stash@{0}", "Stash")]);

    expect([refs.branches, refs.tags].map(names)).toEqual([{ leaves: [], folders: [] }, { leaves: [], folders: [] }]);
    expect(refs.remotes).toEqual([]);
  });

  it("sorts names as people read them, ignoring case", () => {
    const refs = sidebarRefs([ref("v10", "Tag"), ref("V2", "Tag"), ref("v1", "Tag")]);

    expect(names(refs.tags).leaves).toEqual(["v1", "V2", "v10"]);
  });
});
