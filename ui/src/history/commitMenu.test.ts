import { describe, expect, it, vi } from "vitest";
import type { CommitEntry } from "../protocol";
import { commitMenuItems, type MenuContext } from "./commitMenu";
import type { Marker } from "./markers";

const commit: CommitEntry = {
  id: "e83c5163316f89bfbde7d9ab23ca2e25604af290",
  parentIds: [],
  authorName: "Åsa",
  authorEmail: "asa@example.com",
  authoredAt: "2026-10-21T09:00:00+02:00",
  committedAt: "2026-10-21T09:00:00+02:00",
  subject: "Fix the cart",
};

const marker = (name: string, kind: Marker["kind"], worktree: string | null = null): Marker => ({ name, kind, isCurrent: false, worktree });

function context(fields: Partial<MenuContext> = {}): MenuContext {
  return { commit, markers: [], current: { path: "C:\\src\\shop", branch: "main", head: "9fceb02d0ae598e95dc970b74767f19372d61af8" }, localBranches: new Set(["main"]), ...fields };
}

function actions() {
  return { copy: vi.fn(), createBranch: vi.fn(), createTag: vi.fn(), switchTo: vi.fn(), reset: vi.fn() };
}

const labels = (c: MenuContext) => commitMenuItems(c, actions()).map((i) => (i.disabled ? `(${i.label})` : i.label));

describe("a history row's menu", () => {
  it("has only what is safe in this milestone", () => {
    expect(labels(context())).toEqual([
      "Copy hash",
      "Copy subject",
      "Create branch here…",
      "Check out this commit (detached)",
      "Create tag here…",
      "Reset main to here…",
    ]);
  });

  it("offers each branch on the commit, but not the current one or one checked out elsewhere", () => {
    const markers = [marker("main", "Branch"), marker("topic", "Branch"), marker("claude/fix", "Branch", "C:\\src\\shop\\.claude\\worktrees\\fix"), marker("v1", "Tag")];

    expect(labels(context({ markers })).filter((l) => l.includes("Switch"))).toEqual(["Switch to topic", "(Switch to claude/fix (checked out in fix))"]);
  });

  it("offers a remote branch as a new local branch, unless a local one has its name", () => {
    const markers = [marker("origin/main", "RemoteBranch"), marker("origin/feature/åäö", "RemoteBranch")];

    expect(labels(context({ markers })).filter((l) => l.includes("Switch"))).toEqual(["Switch to origin/feature/åäö as new branch feature/åäö"]);
  });

  it("has no reset on a detached HEAD, and no detach onto the commit already checked out that way", () => {
    const detachedHere = context({ current: { path: "C:\\src\\shop", branch: null, head: commit.id } });

    expect(labels(detachedHere).slice(-2)).toEqual(["(Check out this commit (detached))", "Create tag here…"]);
  });

  it("disables reset to the commit the branch is already at", () => {
    expect(labels(context({ current: { path: "C:\\src\\shop", branch: "main", head: commit.id } })).at(-1)).toBe("(Reset main to here…)");
  });

  it("runs each item with the commit or target it names", () => {
    const run = actions();
    const items = commitMenuItems(context({ markers: [marker("origin/topic", "RemoteBranch")] }), run);

    for (const item of items) item.run();

    expect(run.copy.mock.calls).toEqual([
      [commit.id, "the hash"],
      ["Fix the cart", "the subject"],
    ]);
    expect(run.switchTo.mock.calls).toEqual([[{ remoteBranch: "origin/topic" }], [{ commit: commit.id }]]);
    expect([run.createBranch.mock.calls, run.createTag.mock.calls, run.reset.mock.calls]).toEqual([[[commit]], [[commit]], [[commit]]]);
  });
});
