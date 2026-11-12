import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { RpcClient } from "../rpc";
import { FakeSidecar, sampleCommits, sampleState } from "../testing/fakeSidecar";
import { HistoryHarness } from "../testing/HistoryHarness";

// jsdom has no canvas. The graph's drawing has its own tests.
beforeEach(() => void vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null));
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const [merge, root] = sampleCommits as [(typeof sampleCommits)[0], (typeof sampleCommits)[0]];

function setup(sidecar = new FakeSidecar()) {
  render(<HistoryHarness client={new RpcClient(sidecar.transport)} state={sampleState} now={new Date("2026-10-21T09:30:00+02:00")} openedPath={sampleState.repoRoot} storage={{ get: () => null, set: () => {} }} />);
  return sidecar;
}

const list = () => screen.getByRole("listbox", { name: "Commits" });
const row = (index: number) => within(list()).getAllByRole("option")[index]!;
const menu = () => screen.getByRole("menu");
const item = (name: string | RegExp) => within(menu()).getByRole("menuitem", { name });
const dialog = () => screen.getByRole("dialog");

async function ready() {
  await waitFor(() => expect(within(list()).getAllByRole("option")).toHaveLength(2));
}

async function openOnRoot(name: string | RegExp) {
  fireEvent.contextMenu(row(1), { clientX: 40, clientY: 80 });
  fireEvent.click(item(name));
}

const sent = (sidecar: FakeSidecar, method: string) => sidecar.transport.sent.filter((m) => m.method === method).map((m) => m.params);

/** The root commit with a branch on it, as `fix/räksmörgås`. */
function withBranchOnRoot(sidecar: FakeSidecar, name = "fix/räksmörgås") {
  sidecar.refs = [...sidecar.refs, { name, kind: "Branch", targetId: root.id, agent: null }];
  return sidecar;
}

describe("the history row's menu", () => {
  it("opens on a right-click, selects the row and lists what is safe in this milestone", async () => {
    setup();
    await ready();

    fireEvent.contextMenu(row(1), { clientX: 40, clientY: 80 });

    expect(row(1).getAttribute("aria-selected")).toBe("true");
    expect(within(menu()).getAllByRole("menuitem").map((i) => i.textContent)).toEqual([
      "Copy hash",
      "Copy subject",
      "Create branch here…",
      "Check out this commit (detached)",
      "Create tag here…",
      "Reset main to here…",
      "Delete main on origin…",
    ]);
  });

  it("opens from the keyboard on the selected row, and Escape gives focus back to the list", async () => {
    setup();
    await ready();
    list().focus();

    fireEvent.contextMenu(list(), { clientX: 0, clientY: 0 });

    expect(menu().getAttribute("aria-label")).toBe(`Actions for ${merge.id.slice(0, 8)} ${merge.subject}`);
    expect(document.activeElement?.textContent).toBe("Copy hash");
    fireEvent.keyDown(menu(), { key: "Escape" });
    expect(screen.queryByRole("menu")).toBeNull();
    expect(document.activeElement).toBe(list());
  });

  it("creates a branch, saying first that a taken name cannot be used", async () => {
    const sidecar = setup();
    await ready();
    await openOnRoot("Create branch here…");
    const name = within(dialog()).getByRole("textbox", { name: "Name" });
    const create = within(dialog()).getByRole("button", { name: "Create branch" });

    fireEvent.change(name, { target: { value: "main" } });
    expect(await within(dialog()).findByText("There is already a branch named main.")).toBeTruthy();
    expect(create.hasAttribute("disabled")).toBe(true);
    fireEvent.change(name, { target: { value: "fix/räksmörgås" } });
    await waitFor(() => expect(create.hasAttribute("disabled")).toBe(false));
    fireEvent.click(create);

    expect(await screen.findByText(`Created branch fix/räksmörgås at ${root.id.slice(0, 8)}, and switched to it.`)).toBeTruthy();
    // The command window took over from the dialog.
    expect(screen.getByRole("dialog").textContent).toContain("Create branch fix/räksmörgås");
    expect(screen.queryByRole("dialog", { name: /^Create branch at/ })).toBeNull();
    expect(sidecar.refsAsked.at(-1)).toEqual({
      method: "branch.create",
      params: { repoId: "1", worktree: sampleState.repoRoot, commit: root.id, name: "fix/räksmörgås", switch: true, operation: expect.stringMatching(/^op-/) },
    });
  });

  it("creates a tag with its message", async () => {
    const sidecar = setup();
    await ready();
    await openOnRoot("Create tag here…");

    fireEvent.change(within(dialog()).getByRole("textbox", { name: "Name" }), { target: { value: "v2.0" } });
    fireEvent.change(within(dialog()).getByRole("textbox", { name: "Message (optional)" }), { target: { value: "Andra släppet" } });
    await waitFor(() => expect(within(dialog()).getByRole("button", { name: "Create tag" }).hasAttribute("disabled")).toBe(false));
    fireEvent.click(within(dialog()).getByRole("button", { name: "Create tag" }));

    await screen.findByText(`Created tag v2.0 at ${root.id.slice(0, 8)}.`);
    expect(sidecar.refsAsked.at(-1)!.params).toMatchObject({ name: "v2.0", message: "Andra släppet" });
  });

  it("resets after listing the commits that leave the branch, then undoes it", async () => {
    const sidecar = setup();
    await ready();
    await openOnRoot("Reset main to here…");

    expect(dialog().textContent).toContain("1 commit leaves main:");
    expect(within(within(dialog()).getByRole("list", { name: "Commits leaving the branch" })).getByRole("listitem").textContent).toContain(merge.subject);
    fireEvent.click(within(dialog()).getByRole("radio", { name: /Mixed/ }));
    fireEvent.click(within(dialog()).getByRole("button", { name: "Reset" }));

    await screen.findByText(`Reset main (mixed) from ${merge.id.slice(0, 8)} to ${root.id.slice(0, 8)}.`);
    fireEvent.click(screen.getByRole("button", { name: "Undo reset" }));
    await screen.findByText(`Undid the reset: main is back at ${merge.id.slice(0, 8)}.`);
    expect(sidecar.refsAsked.map((a) => a.method)).toEqual(["branch.reset", "branch.undoReset"]);
  });

  it("warns before a hard reset, names it, and undoes it", async () => {
    const sidecar = setup();
    await ready();
    await openOnRoot("Reset main to here…");

    fireEvent.click(within(dialog()).getByRole("radio", { name: /Hard/ }));
    expect(dialog().textContent).toContain("Diffract copies every file that changes first");
    fireEvent.click(within(dialog()).getByRole("button", { name: "Reset" }));

    await screen.findByText(`Reset main (hard) from ${merge.id.slice(0, 8)} to ${root.id.slice(0, 8)}. Nothing uncommitted was lost.`);
    expect(sidecar.refsAsked[0]!.params).toMatchObject({ mode: "Hard" });
    fireEvent.click(screen.getByRole("button", { name: "Undo reset" }));
    await screen.findByText(`Undid the reset: main is back at ${merge.id.slice(0, 8)}.`);
  });

  it("shows git's words when it refuses to switch", async () => {
    const sidecar = new FakeSidecar();
    sidecar.failures.set("branch.switch", {
      error: { code: -32010, message: "Git did not switch, so nothing changed.\n\nerror: Your local changes to the following files would be overwritten by checkout:\n\tsrc/Cart.cs", data: { kind: "ChangeRefused" } },
    });
    setup(sidecar);
    await ready();

    await openOnRoot("Check out this commit (detached)");

    expect((await screen.findByRole("alert")).textContent).toContain("src/Cart.cs");
  });

  it("copies the hash", async () => {
    const writeText = vi.fn(() => Promise.resolve());
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    setup();
    await ready();

    await openOnRoot("Copy hash");

    await screen.findByText("Copied the hash.");
    expect(writeText).toHaveBeenCalledWith(root.id);
  });

  it("deletes a merged branch after a dialog that says no backup is made, then undoes it", async () => {
    const sidecar = withBranchOnRoot(new FakeSidecar());
    setup(sidecar);
    await ready();
    await openOnRoot("Delete branch fix/räksmörgås…");

    const confirm = await screen.findByRole("dialog", { name: "Delete branch fix/räksmörgås?" });
    expect(sent(sidecar, "cleanup.planBranch")).toEqual([{ repoId: sampleState.repoId, branch: "fix/räksmörgås" }]);
    expect(within(confirm).getByText("Delete branch fix/räksmörgås", { selector: "li *" })).toBeTruthy();
    expect(within(confirm).queryByText(/backup tag archive/)).toBeNull();
    expect(confirm.textContent).toContain("no backup tag is made");
    expect(sent(sidecar, "cleanup.apply")).toEqual([]);

    fireEvent.click(within(confirm).getByRole("button", { name: "Delete" }));

    await waitFor(() => expect(sent(sidecar, "cleanup.apply")).toHaveLength(1));
    expect(sidecar.refs.some((r) => r.name === "fix/räksmörgås")).toBe(false);
    fireEvent.click(await screen.findByRole("button", { name: "Undo delete" }));
    await waitFor(() => expect(sent(sidecar, "cleanup.undo")).toHaveLength(1));
    await waitFor(() => expect(sidecar.refs.some((r) => r.name === "fix/räksmörgås")).toBe(true));
  });

  it("lists the backup tag first when the branch has work nowhere else", async () => {
    const sidecar = withBranchOnRoot(new FakeSidecar(), "wip");
    sidecar.cleanups.atRisk.set("wip", 2);
    setup(sidecar);
    await ready();
    await openOnRoot("Delete branch wip…");

    const confirm = await screen.findByRole("dialog", { name: "Delete branch wip?" });

    const steps = within(within(confirm).getByRole("list", { name: "Planned steps" })).getAllByText(/backup tag|Delete branch/).map((e) => e.textContent);
    expect(steps).toEqual(["Keep a backup tag archive/branch/wip-2026-10-21 for 2 commits that exist nowhere else", "Delete branch wip"]);
    expect(confirm.textContent).toContain("a backup tag keeps them first");
  });

  it("removes nothing when the dialog is cancelled", async () => {
    const sidecar = withBranchOnRoot(new FakeSidecar());
    setup(sidecar);
    await ready();
    await openOnRoot("Delete branch fix/räksmörgås…");

    fireEvent.click(within(await screen.findByRole("dialog", { name: "Delete branch fix/räksmörgås?" })).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog", { name: /^Delete branch/ })).toBeNull();
    expect(sent(sidecar, "cleanup.apply")).toEqual([]);
    expect(sidecar.refs.some((r) => r.name === "fix/räksmörgås")).toBe(true);
  });

  it("opens no dialog when the sidecar refuses, because the branch was checked out meanwhile", async () => {
    const sidecar = withBranchOnRoot(new FakeSidecar());
    setup(sidecar);
    await ready();
    sidecar.change(sampleState.repoId, (s) => ({
      ...s,
      snapshot: { ...s.snapshot, worktrees: [...s.snapshot.worktrees, { ...s.snapshot.worktrees[0]!, path: "C:\src\shop\.claude\worktrees\fix", isMain: false, branch: "fix/räksmörgås" }] },
    }));
    await openOnRoot("Delete branch fix/räksmörgås…");

    expect(await screen.findByText("In use, so it is never cleaned up.")).toBeTruthy();
    expect(screen.queryByRole("dialog", { name: /^Delete branch fix/ })).toBeNull();
    expect(sent(sidecar, "cleanup.apply")).toEqual([]);
  });

  it("does not offer a branch that is checked out in a worktree", async () => {
    setup();
    await ready();

    fireEvent.contextMenu(row(0), { clientX: 40, clientY: 80 });

    expect(within(menu()).queryAllByRole("menuitem", { name: /^Delete branch/ })).toEqual([]);
  });
});
