import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import applySample from "../../../tests/Diffract.Sidecar.Tests/Contract/cleanup.apply.json";
import planSample from "../../../tests/Diffract.Sidecar.Tests/Contract/cleanup.plan.json";
import undoSample from "../../../tests/Diffract.Sidecar.Tests/Contract/cleanup.undo.json";
import type { CleanupPlanView, CleanupResult } from "../protocol";
import { CleanupDialog } from "./CleanupDialog";
import { CleanupResultPanel } from "./CleanupResultPanel";

afterEach(cleanup);

// Written by the sidecar's contract tests, from its own serializer.
const plan = (planSample as { result: CleanupPlanView }).result;
const applied = (applySample as { result: CleanupResult }).result;
const undone = (undoSample as { result: CleanupResult }).result;

function showDialog() {
  const onConfirm = vi.fn();
  const onCancel = vi.fn();
  render(<CleanupDialog plan={plan} onConfirm={onConfirm} onCancel={onCancel} />);
  return { onConfirm, onCancel, dialog: screen.getByRole("dialog", { name: "Clean up 2 leftovers?" }) };
}

const planned = (name: string) => within(screen.getByRole("list", { name: "Planned steps" })).getByRole("listitem", { name });

describe("CleanupDialog", () => {
  it("lists every step of every leftover in plain English, in order", () => {
    showDialog();

    expect(within(planned("claude/räksmörgås-fix")).getAllByRole("listitem").map((li) => li.textContent)).toEqual([
      "Keep a backup tag archive/worktree/claude/räksmörgås-fix-2026-10-21 for 2 commits that exist nowhere else",
      "Remove worktree C:\\src\\shop\\.claude\\worktrees\\räksmörgås fix",
      "Delete branch claude/räksmörgås-fix",
    ]);
  });

  it("says before anything runs which steps Diffract cannot undo", () => {
    showDialog();

    expect(planned("old-task").textContent).toContain("Diffract cannot undo this. Restore it from the Recycle Bin.");
    expect(planned("claude/räksmörgås-fix").textContent).not.toContain("cannot undo");
  });

  it("lists a refused leftover with why, and plans nothing for it", () => {
    showDialog();

    expect(within(planned("copilot/fix-login")).queryAllByRole("listitem")).toEqual([]);
    expect(planned("copilot/fix-login").textContent).toContain("Left as it is: In use, so it is never cleaned up.");
  });

  it("gives an optional step its own checkbox, ticked, and leaves it out when unticked", () => {
    const { onConfirm } = showDialog();
    const branch = screen.getByRole("checkbox", { name: "Delete branch claude/räksmörgås-fix" }) as HTMLInputElement;
    expect(branch.checked).toBe(true);
    expect(screen.getAllByRole("checkbox")).toHaveLength(1);

    fireEvent.click(branch);
    fireEvent.click(screen.getByRole("button", { name: "Clean up" }));

    expect(onConfirm).toHaveBeenCalledWith([{ leftover: 0, step: 2 }]);
  });

  it("confirms with nothing left out when every step stays ticked", () => {
    const { onConfirm } = showDialog();

    fireEvent.click(screen.getByRole("button", { name: "Clean up" }));

    expect(onConfirm).toHaveBeenCalledWith([]);
  });

  it("focuses Cancel, so Enter on a dialog that just opened removes nothing, and Escape cancels", () => {
    const { onCancel, onConfirm, dialog } = showDialog();

    expect(document.activeElement).toBe(screen.getByRole("button", { name: "Cancel" }));
    fireEvent.keyDown(dialog, { key: "Escape" });

    expect(onCancel).toHaveBeenCalled();
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it("cannot confirm a plan where every leftover was refused", () => {
    render(<CleanupDialog plan={{ ...plan, leftovers: plan.leftovers.slice(2) }} onConfirm={vi.fn()} onCancel={vi.fn()} />);

    expect((screen.getByRole("button", { name: "Clean up" }) as HTMLButtonElement).disabled).toBe(true);
  });
});

describe("CleanupResultPanel", () => {
  const items = (name: string) => within(screen.getByRole("list", { name })).getAllByRole("listitem").map((li) => li.textContent);

  it("says what was removed, what was kept as a backup, and what was not done and why", () => {
    render(<CleanupResultPanel shown={{ result: applied, undo: false }} canUndo busy={false} onUndo={vi.fn()} onDismiss={vi.fn()} />);

    expect(items("Removed")).toEqual(["Remove worktree C:\\src\\shop\\.claude\\worktrees\\räksmörgås fix", "Delete branch claude/räksmörgås-fix"]);
    expect(items("Kept as a backup")).toEqual(["Keep a backup tag archive/worktree/claude/räksmörgås-fix-2026-10-21 for 2 commits that exist nowhere else"]);
    expect(items("Not done")).toEqual([
      "Move folder C:\\src\\shop\\.claude\\worktrees\\old-task to the Recycle Bin" +
        "Windows did not move C:\\src\\shop\\.claude\\worktrees\\old-task to the Recycle Bin (error 32). A program may be using files in it.",
    ]);
  });

  it("offers Undo while the log has something to take back", () => {
    const onUndo = vi.fn();
    render(<CleanupResultPanel shown={{ result: applied, undo: false }} canUndo busy={false} onUndo={onUndo} onDismiss={vi.fn()} />);

    fireEvent.click(screen.getByRole("button", { name: "Undo" }));

    expect(onUndo).toHaveBeenCalled();
  });

  it("shows an undo as what was put back, with no Undo of its own", () => {
    render(<CleanupResultPanel shown={{ result: undone, undo: true }} canUndo={false} busy={false} onUndo={vi.fn()} onDismiss={vi.fn()} />);

    expect(screen.getByRole("heading", { name: "Undone" })).toBeTruthy();
    expect(items("Put back")).toEqual(["Undo: Delete branch claude/räksmörgås-fix"]);
    expect(items("Not done")).toHaveLength(2);
    expect(screen.queryByRole("button", { name: "Undo" })).toBeNull();
  });
});
