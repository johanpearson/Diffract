import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { App, LastRepoKey } from "../App";
import type { AppHost } from "../host";
import type { Leftover } from "../protocol";
import { RpcClient } from "../rpc";
import { FakeSidecar, sampleState } from "../testing/fakeSidecar";

afterEach(cleanup);

const repoRoot = sampleState.repoRoot;

/** The app on Cleanup with the sample repo open, plus `extra` leftovers. Returns the sidecar and the stored settings. */
async function setup(extra: Leftover[] = []) {
  const sidecar = new FakeSidecar();
  const state = sidecar.repos.get(repoRoot.toLowerCase())!;
  sidecar.repos.set(repoRoot.toLowerCase(), { ...state, leftovers: [...state.leftovers, ...extra] });
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
  return { sidecar, stored };
}

const row = (name: string) => within(screen.getByRole("list", { name: "Leftovers" })).getByRole("listitem", { name });

const stash: Leftover = {
  kind: "Stash",
  name: "stash@{0}",
  path: null,
  agent: null,
  verdict: "NeedsReview",
  commitsAtRisk: 1,
  reason: 'Stash looks made by a tool ("autostash"), 3 days old',
  check: "Look at its changes in History. Dropping it keeps them under a backup tag.",
  commit: "4b825dc642cb6eb9a060e54bf8d69288fbee4904",
};

const orphaned: Leftover = {
  kind: "OrphanedCommit",
  name: "49919a09 Experiment",
  path: null,
  agent: null,
  verdict: "NeedsReview",
  commitsAtRisk: 1,
  reason: "Not on any branch, tag, stash or reflog, so git may delete it in its next cleanup",
  check: "Look at what it holds, then keep it, or let git delete it.",
  commit: "49919a09f2b7c1d0e3a4b5c6d7e8f9a0b1c2d3e4",
};

describe("rows that need review", () => {
  it("say what to check", async () => {
    await setup();

    expect(row("copilot/fix-login").textContent).toContain("Look at the 2 commits only on this branch in History. Deleting it keeps them under a backup tag.");
  });

  it("open to list the commits only they hold, and say when only the newest are shown", async () => {
    const { sidecar } = await setup();

    fireEvent.click(screen.getByRole("button", { name: "Show the commits of copilot/fix-login" }));

    const commits = await screen.findByRole("list", { name: "Commits of copilot/fix-login" });
    expect(within(commits).getAllByRole("listitem").map((li) => li.textContent)).toEqual([
      "e83c5163 Retry the login on räksmörgåsGitHub Copilot, 21 days ago",
      "4b825dc6 (no subject)GitHub Copilot, 22 days ago",
      "Only the newest 2 are shown.",
    ]);
    expect(sidecar.cleanups.asked.find((a) => a.method === "cleanup.inspect")?.params).toEqual({
      repoId: "1",
      leftover: { kind: "LocalBranch", name: "copilot/fix-login", path: null },
    });
    fireEvent.click(screen.getByRole("button", { name: "Hide the commits of copilot/fix-login" }));
    expect(screen.queryByRole("list", { name: "Commits of copilot/fix-login" })).toBeNull();
  });

  it("show a branch in History", async () => {
    await setup();

    fireEvent.click(screen.getByRole("button", { name: "Show copilot/fix-login in History" }));

    expect(screen.getByRole("tab", { name: "History" }).getAttribute("aria-selected")).toBe("true");
  });

  it("turn Stashes on in History to show a stash", async () => {
    const { stored } = await setup([stash]);

    fireEvent.click(screen.getByRole("button", { name: "Show stash@{0} in History" }));

    expect(screen.getByRole("tab", { name: "History" }).getAttribute("aria-selected")).toBe("true");
    expect(stored.get("diffract.history.stashes")).toBe("true");
  });

  it("offer no History for an orphaned commit, which History never lists, but show what it holds", async () => {
    await setup([orphaned]);

    expect(screen.queryByRole("button", { name: "Show 49919a09 Experiment in History" })).toBeNull();
    expect(screen.getByRole("button", { name: "Show the commits of 49919a09 Experiment" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Keep 49919a09 Experiment" })).toBeTruthy();
  });

  it("put every button of a row in one bar", async () => {
    await setup();

    expect(row("räksmörgås fix").querySelectorAll(".item-actions")).toHaveLength(1);
    expect(within(row("old-task")).queryByRole("button", { name: /^Show the commits/ })).toBeNull();
  });
});
