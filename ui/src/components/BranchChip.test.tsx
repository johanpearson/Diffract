import { afterEach, describe, expect, it } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import type { WorktreeChanges } from "../protocol";
import { BranchChip, syncBadge } from "./BranchChip";

afterEach(cleanup);

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

describe("the sync badge", () => {
  it.each([
    [{}, "✓"],
    [{ ahead: 2 }, "↑2"],
    [{ behind: 3 }, "↓3"],
    [{ ahead: 1, behind: 4 }, "↑1 ↓4"],
    [{ upstream: null, ahead: null, behind: null }, "no upstream"],
    [{ ahead: null, behind: null }, "gone"],
  ])("shows %o as %s", (fields, badge) => {
    expect(syncBadge(status(fields))).toBe(badge);
  });
});

describe("the branch chip", () => {
  it("keeps the full branch name as its tooltip, so an ellipsis loses nothing", () => {
    const name = "issue-77/history-delete-branch-with-a-very-long-name";
    render(<BranchChip status={status({ branch: name })} />);

    expect(screen.getByText(name).getAttribute("title")).toBe(name);
  });

  it("says where a detached head sits", () => {
    render(<BranchChip status={status({ branch: null })} />);

    expect(screen.getByText("detached at 4b825dc6")).toBeTruthy();
  });

  it("spells the badge out for a screen reader and the tooltip", () => {
    render(<BranchChip status={status({ ahead: 2, behind: 1 })} />);

    const badge = screen.getByRole("img", { name: "2 ahead, 1 behind origin/main" });
    expect(badge.getAttribute("title")).toBe("2 ahead, 1 behind origin/main");
    expect(badge.textContent).toBe("↑2 ↓1");
  });
});
