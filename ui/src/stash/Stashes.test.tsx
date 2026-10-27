import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App, LastRepoKey } from "../App";
import type { AppHost } from "../host";
import { RpcClient } from "../rpc";
import { FakeSidecar, sampleState } from "../testing/fakeSidecar";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

// The contract sample's one stash: stash@{0}, "On claude/räksmörgås-fix: Halvfärdigt".
const stashCommit = "e83c5163316f89bfbde7d9ab23ca2e25604af290";
const agentWorktree = "C:\\src\\shop\\.claude\\worktrees\\räksmörgås fix";

async function openRepo(sidecar = new FakeSidecar()) {
  vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null);
  const stored = new Map<string, string>([[LastRepoKey, sampleState.repoRoot]]);
  const host: AppHost = {
    transport: sidecar.transport,
    pickFolder: vi.fn(async () => null),
    onSidecarEvent: () => () => {},
    restartSidecar: vi.fn(async () => {}),
    storage: { get: (key) => stored.get(key) ?? null, set: (key, value) => void stored.set(key, value) },
  };
  render(<App host={host} client={new RpcClient(sidecar.transport)} clock={() => new Date("2026-10-21T09:30:00+02:00")} autoCloseMs={20} />);
  await within(await screen.findByRole("navigation", { name: "Repository" })).findByRole("button", { name: "main" });
  return { sidecar, stored };
}

const sidebar = () => screen.getByRole("navigation", { name: "Repository" });
const stashList = () => within(sidebar()).queryByRole("list", { name: "Stashes" });
const stashNames = () => (stashList() ? within(stashList()!).queryAllByRole("button").map((b) => b.textContent) : []);
const sent = (sidecar: FakeSidecar, method: string) => sidecar.transport.sent.filter((m) => m.method === method).map((m) => m.params);
const commandWindow = () => screen.getByRole("dialog", { name: /stash|drop/i });

function openStashes() {
  fireEvent.click(within(sidebar()).getByRole("button", { name: /^Stashes(, \d+)?$/ }));
}

function menuItem(name: string | RegExp) {
  fireEvent.contextMenu(within(stashList()!).getByRole("button", { name: "Halvfärdigt" }), { clientX: 40, clientY: 300 });
  return within(screen.getByRole("menu", { name: "Actions for stash Halvfärdigt" })).getByRole("menuitem", { name });
}

describe("Stashes in the sidebar", () => {
  it("are closed at first, and list each stash by its message", async () => {
    const { stored } = await openRepo();
    expect(stashList()).toBeNull();

    openStashes();

    expect(stashNames()).toEqual(["Halvfärdigt"]);
    expect(stored.get("diffract.sidebar.open.stashes")).toBe("true");
  });

  it("shows a stash in History with Stashes turned on when it is clicked", async () => {
    const { sidecar } = await openRepo();
    openStashes();

    fireEvent.click(within(stashList()!).getByRole("button", { name: "Halvfärdigt" }));

    expect(screen.getByRole("tab", { name: "History" }).getAttribute("aria-selected")).toBe("true");
    await waitFor(() => expect([...sent(sidecar, "history.read"), ...sent(sidecar, "history.update")]).toContainEqual(expect.objectContaining({ stashes: true })));
  });

  it("applies a stash by its commit to the worktree Changes shows, and keeps it", async () => {
    const { sidecar } = await openRepo();
    openStashes();

    fireEvent.click(menuItem("Apply to main"));

    await screen.findByText("Applied the stash. It is still in the list.");
    expect(sent(sidecar, "stash.apply")).toEqual([expect.objectContaining({ repoId: sampleState.repoId, worktree: sampleState.repoRoot, commit: stashCommit })]);
    expect(stashNames()).toEqual(["Halvfärdigt"]);
  });

  it("applies to another worktree once Changes shows it, but not while an agent works there", async () => {
    await openRepo();
    fireEvent.click(screen.getByRole("tab", { name: "Changes" }));
    fireEvent.change(await screen.findByRole("combobox"), { target: { value: agentWorktree } });
    openStashes();

    const apply = menuItem(/^Apply to claude\/räksmörgås-fix \(an agent is working there\)$/);

    expect((apply as HTMLButtonElement).disabled).toBe(true);
  });

  it("drops a stash after the dialog, with a backup tag first, and undoes the drop from the command window", async () => {
    const { sidecar } = await openRepo();
    openStashes();

    fireEvent.click(menuItem("Drop…"));
    const dialog = await screen.findByRole("dialog", { name: "Drop this stash?" });
    expect(within(dialog).getByText(/Keep a backup tag archive\/stash\//)).toBeTruthy();
    expect(within(dialog).getByText("Undo puts it back on top of the list.")).toBeTruthy();
    await waitFor(() => expect(document.activeElement).toBe(within(dialog).getByRole("button", { name: "Cancel" })));
    fireEvent.click(within(dialog).getByRole("button", { name: "Drop" }));

    await waitFor(() => expect(stashNames()).toEqual([]));
    fireEvent.click(await within(commandWindow()).findByRole("button", { name: "Undo drop" }));

    await waitFor(() => expect(stashNames()).toEqual(["Halvfärdigt"]));
    expect(sent(sidecar, "cleanup.planStash")).toEqual([{ repoId: sampleState.repoId, commit: stashCommit }]);
    expect(sent(sidecar, "cleanup.undo")).toHaveLength(1);
  });

  it("drops nothing when the dialog is cancelled", async () => {
    const { sidecar } = await openRepo();
    openStashes();

    fireEvent.click(menuItem("Drop…"));
    fireEvent.click(within(await screen.findByRole("dialog", { name: "Drop this stash?" })).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog", { name: "Drop this stash?" })).toBeNull();
    expect(sent(sidecar, "cleanup.apply")).toEqual([]);
    expect(stashNames()).toEqual(["Halvfärdigt"]);
  });

  it("apply and drop asks to drop only after a clean apply", async () => {
    await openRepo();
    openStashes();

    fireEvent.click(menuItem("Apply to main and drop"));

    expect(await screen.findByRole("dialog", { name: "Drop this stash?" })).toBeTruthy();
  });

  it("apply and drop keeps the stash when the apply conflicts, and says so", async () => {
    const sidecar = new FakeSidecar();
    sidecar.stashes.conflicts.add(stashCommit);
    await openRepo(sidecar);
    openStashes();

    fireEvent.click(menuItem("Apply to main and drop"));

    expect(await screen.findByText(/Applied the stash with conflicts\. .* It was not dropped\./)).toBeTruthy();
    expect(screen.queryByRole("dialog", { name: "Drop this stash?" })).toBeNull();
    expect(sent(sidecar, "cleanup.planStash")).toEqual([]);
  });
});
