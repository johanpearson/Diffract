import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App, LastRepoKey } from "../App";
import type { AppHost } from "../host";
import type { Leftover } from "../protocol";
import { FlashMs } from "../model/rows";
import { RpcClient } from "../rpc";
import { FakeSidecar, sampleState } from "../testing/fakeSidecar";

afterEach(cleanup);

const repoRoot = sampleState.repoRoot;

/** The app on "Worktrees and agents" with the sample repo open, against a fake sidecar. */
async function setup(sidecar = new FakeSidecar()) {
  const stored = new Map([[LastRepoKey, repoRoot]]);
  const host: AppHost = {
    transport: sidecar.transport,
    pickFolder: vi.fn(async () => null),
    onSidecarEvent: () => () => {},
    restartSidecar: vi.fn(async () => {}),
    storage: { get: (key) => stored.get(key) ?? null, set: (key, value) => void stored.set(key, value) },
  };
  render(<App host={host} client={new RpcClient(sidecar.transport)} clock={() => new Date("2026-10-21T09:30:00+02:00")} initialView="leftovers" autoCloseMs={0} />);
  await screen.findByRole("list", { name: "Leftovers" });
  return sidecar;
}

const rowNames = () => within(screen.getByRole("list", { name: "Leftovers" })).getAllByRole("listitem").map((li) => li.getAttribute("aria-label"));
const tick = (name: string) => fireEvent.click(screen.getByRole("checkbox", { name: `Select ${name}` }));
const button = (name: string | RegExp) => screen.getByRole("button", { name }) as HTMLButtonElement;

/** Ticks the rows, opens the dialog and confirms it. */
async function cleanUp(...names: string[]) {
  names.forEach(tick);
  fireEvent.click(button(`Clean up ${names.length} selected`));
  const dialog = await screen.findByRole("dialog");
  fireEvent.click(within(dialog).getByRole("button", { name: "Clean up" }));
  await screen.findByRole("region", { name: "Cleanup result" });
}

const merged: Leftover = {
  kind: "LocalBranch",
  name: "copilot/done",
  path: null,
  agent: "GitHub Copilot",
  verdict: "SafeToRemove",
  commitsAtRisk: 0,
  reason: "Merged into main",
};

describe("cleaning up", () => {
  it("gives every row a checkbox, apart from those in use", async () => {
    await setup();

    expect(screen.getByRole("checkbox", { name: "Select old-task" })).toBeTruthy();
    expect(screen.getByRole("checkbox", { name: "Select copilot/fix-login" })).toBeTruthy();
    expect(screen.queryByRole("checkbox", { name: "Select räksmörgås fix" })).toBeNull();
    expect(button("Clean up selected").disabled).toBe(true);
  });

  it("plans what was ticked, applies that plan by its id, and the rows go", async () => {
    const sidecar = await setup();

    await cleanUp("old-task", "copilot/fix-login");

    const [plan, apply] = sidecar.cleanups.asked.filter((a) => a.method !== "cleanup.history");
    expect(plan?.params).toEqual({
      repoId: "1",
      leftovers: [
        { kind: "LeftoverFolder", name: "old-task", path: "C:\\src\\shop\\.claude\\worktrees\\old-task" },
        { kind: "LocalBranch", name: "copilot/fix-login", path: null },
      ],
    });
    expect(apply?.params).toMatchObject({ repoId: "1", planId: "plan-1", leaveOut: [] });
    // Removed rows fade out first, as for any change the watcher reports.
    await waitFor(() => expect(rowNames()).toEqual(["räksmörgås fix"]), { timeout: FlashMs * 3 });
    expect(within(screen.getByRole("list", { name: "Kept as a backup" })).getByRole("listitem").textContent).toContain("for 2 commits that exist nowhere else");
  });

  it("undoes from the result, and the rows come back", async () => {
    await setup();
    await cleanUp("copilot/fix-login");

    fireEvent.click(within(screen.getByRole("region", { name: "Cleanup result" })).getByRole("button", { name: "Undo" }));

    await screen.findByRole("region", { name: "Undo result" });
    await waitFor(() => expect(rowNames()).toContain("copilot/fix-login"));
    const past = within(screen.getByRole("list", { name: "Past cleanups" }));
    expect(past.getAllByRole("listitem").map((li) => li.getAttribute("aria-label"))).toEqual(["Undo, just now", "Cleanup, just now"]);
    expect(past.queryByRole("button", { name: /^Undo the cleanup/ })).toBeNull();
  });

  it("lists past cleanups with Undo on each one git can still take back", async () => {
    await setup();
    await cleanUp("copilot/fix-login");
    fireEvent.click(within(screen.getByRole("region", { name: "Cleanup result" })).getByRole("button", { name: "Close" }));

    fireEvent.click(button("Undo the cleanup of copilot/fix-login"));

    await waitFor(() => expect(rowNames()).toContain("copilot/fix-login"));
  });

  it("says what was skipped and why, and the row stays", async () => {
    const sidecar = new FakeSidecar();
    sidecar.cleanups.skips.set("copilot/fix-login", "Branch copilot/fix-login moved: it is at 1a2b3c4d now, not 9fceb02d.");
    await setup(sidecar);

    await cleanUp("copilot/fix-login");

    const notDone = within(screen.getByRole("list", { name: "Not done" })).getAllByRole("listitem").map((li) => li.textContent);
    expect(notDone[0]).toContain("Branch copilot/fix-login moved: it is at 1a2b3c4d now, not 9fceb02d.");
    expect(notDone[1]).toContain("Skipped, as an earlier step for the same leftover did not run.");
    expect(rowNames()).toContain("copilot/fix-login");
  });

  it("selects all merged branches and nothing else", async () => {
    const sidecar = new FakeSidecar();
    const state = sidecar.repos.get(repoRoot.toLowerCase())!;
    sidecar.repos.set(repoRoot.toLowerCase(), { ...state, leftovers: [...state.leftovers, merged] });
    await setup(sidecar);

    fireEvent.click(button("Select all merged branches"));

    expect(screen.getAllByRole("checkbox").filter((c) => (c as HTMLInputElement).checked).map((c) => c.getAttribute("aria-label"))).toEqual(["Select copilot/done"]);
    expect(button("Clean up 1 selected").disabled).toBe(false);
  });

  it("drops a ticked row that went into use while it was ticked", async () => {
    const sidecar = await setup();
    tick("copilot/fix-login");
    expect(button("Clean up 1 selected")).toBeTruthy();

    act(() =>
      sidecar.change("1", (s) => ({ ...s, leftovers: s.leftovers.map((l) => (l.name === "copilot/fix-login" ? { ...l, verdict: "InUse" as const } : l)) })),
    );

    expect(button("Clean up selected").disabled).toBe(true);
    expect(screen.queryByRole("checkbox", { name: "Select copilot/fix-login" })).toBeNull();
  });

  it("closes the dialog on Cancel and removes nothing", async () => {
    const sidecar = await setup();
    tick("old-task");
    fireEvent.click(button("Clean up 1 selected"));
    await screen.findByRole("dialog");

    fireEvent.click(button("Cancel"));

    expect(screen.queryByRole("dialog")).toBeNull();
    expect(sidecar.cleanups.asked.map((a) => a.method)).not.toContain("cleanup.apply");
    expect(rowNames()).toHaveLength(3);
  });
});
