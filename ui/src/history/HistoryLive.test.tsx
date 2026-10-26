import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { CommitEntry, HistoryRef, RepoState } from "../protocol";
import { RpcClient } from "../rpc";
import { FakeSidecar, sampleState } from "../testing/fakeSidecar";
import { HistoryHarness } from "../testing/HistoryHarness";

// jsdom has no canvas. The graph's drawing has its own tests.
beforeEach(() => void vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null));
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function commit(id: string, parentIds: string[] = [], subject = `Commit ${id}`): CommitEntry {
  return { id, parentIds, authorName: "Johan", authorEmail: "johan@example.com", authoredAt: "2026-10-21T08:00:00+02:00", committedAt: "2026-10-21T08:00:00+02:00", subject };
}

function ref(name: string, targetId: string, kind: HistoryRef["kind"] = "Branch", agent: string | null = null): HistoryRef {
  return { name, kind, targetId, agent };
}

/** A repo whose worktrees point at no commit in these histories, so only the refs decide what is shown. */
const state: RepoState = { ...sampleState, snapshot: { ...sampleState.snapshot, worktrees: [] } };

function setup(history: CommitEntry[], refs: HistoryRef[], stored: Record<string, string> = {}) {
  const sidecar = new FakeSidecar();
  sidecar.history = history;
  sidecar.refs = refs;
  sidecar.agentHeads = [];
  const saved = new Map(Object.entries(stored));
  const storage = { get: (key: string) => saved.get(key) ?? null, set: (key: string, value: string) => void saved.set(key, value) };
  const client = new RpcClient(sidecar.transport);
  const view = (s: RepoState) => <HistoryHarness client={client} state={s} now={new Date("2026-10-21T09:30:00+02:00")} openedPath={s.repoRoot} storage={storage} />;
  const { rerender } = render(view(state));
  let snapshots = 0;
  /** As after a repo.changed: the same repo, a newer snapshot. */
  const changed = () => rerender(view({ ...state, takenAt: `2026-10-21T09:${String(++snapshots).padStart(2, "0")}:00Z` }));
  return { sidecar, saved, changed };
}

const list = () => screen.getByRole("listbox", { name: "Commits" });
const subjects = () => within(list()).getAllByRole("option").map((o) => o.querySelector(".subject")!.textContent);
const selected = () => within(list()).getByRole("option", { selected: true }).querySelector(".subject")!.textContent;
const row = (subject: string) => within(list()).getAllByRole("option").find((o) => o.querySelector(".subject")!.textContent === subject)!;
const toggle = (name: string) => screen.getByRole("checkbox", { name });

async function loaded(count: string) {
  await waitFor(() => expect(screen.getByRole("status").textContent).toBe(count));
}

describe("live history", () => {
  it("adds new commits on top and keeps the selection on the commit it was on", async () => {
    const { sidecar, changed } = setup([commit("c2", ["c1"]), commit("c1")], [ref("main", "c2")]);
    await loaded("2 commits");
    fireEvent.click(row("Commit c1"));

    sidecar.history = [commit("c4", ["c3"]), commit("c3", ["c2"]), ...sidecar.history];
    sidecar.refs = [ref("main", "c4")];
    changed();

    await loaded("4 commits");
    expect(subjects()).toEqual(["Commit c4", "Commit c3", "Commit c2", "Commit c1"]);
    expect(selected()).toBe("Commit c1");
    expect(sidecar.updatesAsked).toHaveLength(1);
  });

  it("keeps the first commit selected, rather than the new top one, until the user picks another", async () => {
    const { sidecar, changed } = setup([commit("c1")], [ref("main", "c1")]);
    await loaded("1 commit");

    sidecar.history = [commit("c2", ["c1"]), ...sidecar.history];
    sidecar.refs = [ref("main", "c2")];
    changed();

    await loaded("2 commits");
    expect(selected()).toBe("Commit c1");
  });

  it("moves a branch's marker with it", async () => {
    const { sidecar, changed } = setup([commit("c1")], [ref("main", "c1")]);
    await loaded("1 commit");

    sidecar.history = [commit("c2", ["c1"]), ...sidecar.history];
    sidecar.refs = [ref("main", "c2")];
    changed();

    await waitFor(() => expect(row("Commit c2").textContent).toContain("main"));
    expect(row("Commit c1").textContent).not.toContain("main");
  });

  it("removes the commits a force-moved branch left behind", async () => {
    const { sidecar, changed } = setup([commit("f", ["c1"]), commit("c1")], [ref("main", "c1"), ref("feature", "f")]);
    await loaded("2 commits");

    sidecar.refs = [ref("main", "c1"), ref("feature", "c1")];
    changed();

    await loaded("1 commit");
    expect(subjects()).toEqual(["Commit c1"]);
  });

  it("asks for nothing while the snapshot stays the same", async () => {
    const { sidecar } = setup([commit("c1")], [ref("main", "c1")]);
    await loaded("1 commit");

    await new Promise((resolve) => setTimeout(resolve, 20));

    expect(sidecar.updatesAsked).toEqual([]);
  });

  it("reads everything again when too much changed, and shows the old history until then", async () => {
    const { sidecar, changed } = setup([commit("c1")], [ref("main", "c1")]);
    await loaded("1 commit");
    const seen: number[] = [];
    const observer = new MutationObserver(() => seen.push(within(list()).queryAllByRole("option").length));
    observer.observe(list(), { childList: true, subtree: true });

    sidecar.rereadNext = true;
    sidecar.history = [commit("c3", ["c2"]), commit("c2", ["c1"]), ...sidecar.history];
    sidecar.refs = [ref("main", "c3")];
    changed();

    await loaded("3 commits");
    observer.disconnect();
    expect(subjects()).toEqual(["Commit c3", "Commit c2", "Commit c1"]);
    expect(seen.every((count) => count >= 1)).toBe(true);
  });
});

describe("history toggles", () => {
  const withCheckpoint = () =>
    setup([commit("cp", ["c1"], "copilot checkpoint: saved"), commit("c1")], [ref("main", "c1"), ref("copilot/checkpoints/s/1", "cp", "Checkpoint", "GitHub Copilot")]);

  it("shows checkpoints only when asked, and remembers the choice", async () => {
    const { saved } = withCheckpoint();
    await loaded("1 commit");
    expect(toggle("Checkpoints")).toHaveProperty("checked", false);

    fireEvent.click(toggle("Checkpoints"));

    await loaded("2 commits");
    expect(row("copilot checkpoint: saved").textContent).toContain("copilot/checkpoints/s/1");
    expect(saved.get("diffract.history.checkpoints")).toBe("true");

    fireEvent.click(toggle("Checkpoints"));

    await loaded("1 commit");
  });

  it("reads checkpoints from the start when they were on last time", async () => {
    setup([commit("cp", ["c1"]), commit("c1")], [ref("main", "c1"), ref("copilot/checkpoints/s/1", "cp", "Checkpoint")], { "diffract.history.checkpoints": "true" });

    await loaded("2 commits");
    expect(toggle("Checkpoints")).toHaveProperty("checked", true);
  });

  it("marks stashes only when asked", async () => {
    setup([commit("st", ["c1"], "On main: work"), commit("c1")], [ref("main", "c1"), ref("stash@{0}", "st", "Stash")]);
    await loaded("1 commit");

    fireEvent.click(toggle("Stashes"));

    await loaded("2 commits");
    expect(row("On main: work").textContent).toContain("stash@{0}");
  });

  it("fades merged branches by default, and not once turned off", async () => {
    setup([commit("m", ["b", "s"], "Merge"), commit("s", ["b"], "Side"), commit("b", [], "Base")], [ref("main", "m")]);
    await loaded("3 commits");
    expect(toggle("Fade merged branches")).toHaveProperty("checked", true);
    expect(row("Side").className).toContain("faded");
    expect(row("Merge").className).not.toContain("faded");

    fireEvent.click(toggle("Fade merged branches"));

    expect(row("Side").className).not.toContain("faded");
  });

  it("fades everything but agent work when asked", async () => {
    setup([commit("a", ["b"], "Agent fix"), commit("b", [], "Base")], [ref("main", "b"), ref("claude/fix", "a", "Branch", "Claude Code")]);
    await loaded("2 commits");

    fireEvent.click(toggle("Agent work only"));

    expect(row("Agent fix").className).not.toContain("faded");
    expect(row("Base").className).toContain("faded");
  });
});
