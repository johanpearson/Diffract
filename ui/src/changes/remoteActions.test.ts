import { describe, expect, it } from "vitest";
import type { WorktreeChanges } from "../protocol";
import { pullAction, pushAction, toneOf } from "./remoteActions";

const status = (fields: Partial<WorktreeChanges>): WorktreeChanges => ({
  worktree: "C:\\src\\shop",
  branch: "main",
  headCommit: "4b825dc642cb6eb9a060e54bf8d69288fbee4904",
  upstream: "origin/main",
  ahead: 0,
  behind: 0,
  headOnRemote: null,
  files: [],
  ...fields,
});

describe("push", () => {
  it.each([
    [{}, "Push to origin/main", null],
    [{ upstream: null }, "Push to new origin/main", null],
    [{ branch: "feature/åäö", upstream: "upstream/feature/åäö" }, "Push to upstream/feature/åäö", null],
    [{ branch: "topic" }, "Push", "topic follows origin/main, which has another name. Push it from a terminal."],
    [{ branch: null }, "Push", "No branch with commits is checked out here."],
    [{ headCommit: null }, "Push", "No branch with commits is checked out here."],
  ])("%o is %s", (fields, label, reason) => {
    expect(pushAction(status(fields))).toEqual({ label, reason });
  });
});

describe("pull", () => {
  it.each([
    [{}, null],
    [{ upstream: null }, "main does not follow a branch on a remote yet. Push it first."],
    [{ branch: null }, "No branch is checked out here."],
  ])("%o", (fields, reason) => {
    expect(pullAction(status(fields))).toEqual({ label: "Pull", reason });
  });
});

describe("results", () => {
  it("are quiet when something or nothing was done, and stay when the user has to act", () => {
    expect([toneOf("Done"), toneOf("UpToDate"), toneOf("Diverged"), toneOf("Rejected")]).toEqual(["ok", "ok", "warn", "warn"]);
  });
});
