import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { RpcClient } from "../rpc";
import { FakeSidecar, sampleCommits, sampleState } from "../testing/fakeSidecar";
import { HistoryView } from "./HistoryView";

// jsdom has no canvas. The graph's drawing has its own tests.
beforeEach(() => void vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null));
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const [merge, root] = sampleCommits as [(typeof sampleCommits)[0], (typeof sampleCommits)[0]];

function setup(sidecar = new FakeSidecar()) {
  render(<HistoryView client={new RpcClient(sidecar.transport)} state={sampleState} now={new Date("2026-10-21T09:30:00+02:00")} openedPath={sampleState.repoRoot} storage={{ get: () => null, set: () => {} }} />);
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
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(sidecar.refsAsked.at(-1)).toEqual({ method: "branch.create", params: { repoId: "1", worktree: sampleState.repoRoot, commit: root.id, name: "fix/räksmörgås", switch: true } });
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
});
