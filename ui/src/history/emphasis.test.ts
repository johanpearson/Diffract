import { describe, expect, it } from "vitest";
import type { CommitEntry, HistoryRef, HistoryRefs } from "../protocol";
import { fadedCommits, type FadeToggles } from "./emphasis";

function commit(id: string, parentIds: string[] = [], subject = `Commit ${id}`): CommitEntry {
  return { id, parentIds, authorName: "A", authorEmail: "a@example.com", authoredAt: "2026-10-22T09:00:00Z", committedAt: "2026-10-22T09:00:00Z", subject };
}

function ref(name: string, targetId: string, agent: string | null = null, kind: HistoryRef["kind"] = "Branch"): HistoryRef {
  return { name, kind, targetId, agent };
}

function refsOf(refs: HistoryRef[], agentHeads: string[] = [], agentSubjectPrefixes: string[] = []): HistoryRefs {
  return { refs, agentHeads, agentSubjectPrefixes };
}

/**
 * The visual plan's history, in short:
 *
 *   a       claude/fix (agent), on top of m2
 *   m2      main: merges c2 from copilot/deps
 *   c2      origin/copilot/deps (agent)
 *   f1      feature, from m1
 *   m1      main
 *   r       root
 */
const commits = [commit("a", ["m2"]), commit("m2", ["m1", "c2"]), commit("c2", ["m1"]), commit("f1", ["m1"]), commit("m1", ["r"]), commit("r")];
const refs = refsOf([ref("claude/fix", "a", "Claude Code"), ref("main", "m2"), ref("origin/copilot/deps", "c2", "GitHub Copilot", "RemoteBranch"), ref("feature", "f1")]);

const faded = (toggles: Partial<FadeToggles>, r: HistoryRefs = refs, list: CommitEntry[] = commits) =>
  [...fadedCommits(list, r, "main", { fadeMerged: false, agentsOnly: false, ...toggles })].sort();

describe("fading merged branches", () => {
  it("fades what reached main through a merge, but not main's own line or unmerged work", () => {
    expect(faded({ fadeMerged: true })).toEqual(["c2"]);
  });

  it("fades the whole merged branch, however long", () => {
    const list = [commit("m", ["b", "s2"]), commit("s2", ["s1"]), commit("s1", ["b"]), commit("b")];
    expect(faded({ fadeMerged: true }, refsOf([ref("main", "m")]), list)).toEqual(["s1", "s2"]);
  });

  it("fades nothing when main is not among the refs", () => {
    expect(faded({ fadeMerged: true }, refsOf([ref("feature", "f1")]))).toEqual([]);
  });

  it("fades nothing when both toggles are off", () => {
    expect(faded({})).toEqual([]);
  });
});

describe("agent work only", () => {
  it("fades everything but the commits on agents' branches, back to main's line", () => {
    expect(faded({ agentsOnly: true })).toEqual(["f1", "m1", "m2", "r"]);
  });

  it("counts a commit whose subject has an agent's prefix as agent work, wherever it is", () => {
    const list = [commit("m", ["b"], "copilot checkpoint: saved"), commit("b")];
    expect(faded({ agentsOnly: true }, refsOf([ref("main", "m")], [], ["copilot checkpoint: "]), list)).toEqual(["b"]);
  });

  it("counts the commits under an agent's worktree HEAD as agent work", () => {
    expect(faded({ agentsOnly: true }, refsOf([ref("main", "m2")], ["f1"]))).toEqual(["a", "c2", "m1", "m2", "r"]);
  });

  it("follows an agent branch to its root when there is no main", () => {
    const list = [commit("x", ["y"]), commit("y")];
    expect(faded({ agentsOnly: true }, refsOf([ref("claude/x", "x", "Claude Code")]), list)).toEqual([]);
  });

  it("combines with fading merged branches", () => {
    expect(faded({ agentsOnly: true, fadeMerged: true })).toEqual(["c2", "f1", "m1", "m2", "r"]);
  });
});

describe("a partly loaded history", () => {
  it("stops at parents that are not loaded yet", () => {
    const list = [commit("m", ["b", "missing"]), commit("b", ["also-missing"])];
    expect(faded({ fadeMerged: true, agentsOnly: true }, refsOf([ref("main", "m"), ref("claude/z", "missing", "Claude Code")]), list)).toEqual(["b", "m"]);
  });
});
