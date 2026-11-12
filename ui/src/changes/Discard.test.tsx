import { useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ChangeAction, FileSides } from "../history/monacoEditor";
import { RpcClient } from "../rpc";
import { FakeSidecar, sampleState } from "../testing/fakeSidecar";
import { CommandsProvider } from "../commands/Commands";
import { ChangesView } from "./ChangesView";

// jsdom runs no Monaco. This stand-in records the change buttons the view gives the diff.
const monaco = vi.hoisted(() => ({ shown: [] as FileSides[], actions: [] as readonly ChangeAction[] }));
vi.mock("../history/monacoEditor", () => ({
  createFileView: () => ({
    show: (sides: FileSides) => void monaco.shown.push(sides),
    setLayout: () => {},
    setDark: () => {},
    goToChange: () => {},
    setActions: (actions: readonly ChangeAction[]) => void (monaco.actions = actions),
    revealLine: () => {},
    dispose: () => {},
  }),
}));

beforeEach(() => {
  monaco.shown = [];
  monaco.actions = [];
});
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const mainWorktree = "C:\\src\\shop";
const storage = { get: () => null, set: () => {} };

function setup(sidecar = new FakeSidecar()) {
  const client = new RpcClient(sidecar.transport);
  render(
    <CommandsProvider client={client} autoCloseMs={20}>
      <Changes client={client} />
    </CommandsProvider>,
  );
  return sidecar;
}

function Changes({ client }: { client: RpcClient }) {
  const [worktree, setWorktree] = useState(mainWorktree);
  return <ChangesView client={client} state={sampleState} worktree={worktree} onWorktree={setWorktree} storage={storage} />;
}

const lists = () => screen.getByRole("listbox", { name: "Changed files" });
const group = (name: "Unstaged" | "Staged") => within(lists()).getByRole("group", { name: new RegExp(`^${name}`) });
const paths = (name: "Unstaged" | "Staged") => within(group(name)).queryAllByRole("option").map((o) => o.getAttribute("title"));
const discardButton = (path: string) => within(lists()).queryByRole("button", { name: `Discard ${path}` });
const dialog = () => screen.findByRole("dialog", { name: /^Discard/ });
const sent = (sidecar: FakeSidecar, method: string) => sidecar.transport.sent.filter((m) => m.method === method).map((m) => m.params);

async function ready() {
  await screen.findByRole("listbox", { name: "Changed files" });
}

describe("Discard in the Changes view", () => {
  it("offers Discard on unstaged rows only", async () => {
    setup();
    await ready();

    expect(discardButton("src/Cart.cs")).not.toBeNull();
    expect(discardButton("docs/notes.md")).not.toBeNull();
    expect(discardButton("README.md")).toBeNull();
    expect(within(group("Staged")).queryByRole("button", { name: /^Discard/ })).toBeNull();
  });

  it("offers no Discard on a conflicted file", async () => {
    const sidecar = new FakeSidecar();
    const worktree = sidecar.worktree(mainWorktree);
    worktree.status = { ...worktree.status, files: [{ path: "src/Conflict.cs", oldPath: null, kind: "Conflicted", staged: null, unstaged: null, headId: null, indexId: null }] };
    setup(sidecar);
    await ready();

    expect(discardButton("src/Conflict.cs")).toBeNull();
  });

  it("plans first and throws nothing away until the dialog is confirmed", async () => {
    const sidecar = setup();
    await ready();

    fireEvent.click(discardButton("src/Cart.cs")!);
    const confirm = await dialog();

    expect(sent(sidecar, "cleanup.planDiscard")).toEqual([{ repoId: sampleState.repoId, worktree: mainWorktree, paths: ["src/Cart.cs"] }]);
    expect(within(confirm).getByText("Discard all unstaged changes in src/Cart.cs")).toBeTruthy();
    expect(within(confirm).getByText(/Staged changes stay/)).toBeTruthy();
    expect(within(confirm).getByText(/for as long as Diffract stays open/)).toBeTruthy();
    expect(sent(sidecar, "cleanup.apply")).toEqual([]);
    expect(paths("Unstaged")).toContain("src/Cart.cs");
  });

  it("changes nothing when the dialog is cancelled, and focuses Cancel so Enter never discards", async () => {
    const sidecar = setup();
    await ready();
    fireEvent.click(discardButton("src/Cart.cs")!);
    const confirm = await dialog();

    expect(document.activeElement).toBe(within(confirm).getByRole("button", { name: "Cancel" }));
    fireEvent.click(within(confirm).getByRole("button", { name: "Cancel" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(sent(sidecar, "cleanup.apply")).toEqual([]);
    expect(paths("Unstaged")).toContain("src/Cart.cs");
  });

  it("discards a file after the dialog, and Undo discard brings it back", async () => {
    const sidecar = setup();
    await ready();
    fireEvent.click(discardButton("src/Cart.cs")!);
    fireEvent.click(within(await dialog()).getByRole("button", { name: "Discard" }));

    await waitFor(() => expect(paths("Unstaged")).not.toContain("src/Cart.cs"));
    expect(sent(sidecar, "cleanup.apply")).toHaveLength(1);
    expect(paths("Unstaged")).toContain("docs/notes.md");

    fireEvent.click(await screen.findByRole("button", { name: "Undo discard" }));

    await waitFor(() => expect(paths("Unstaged")).toContain("src/Cart.cs"));
    expect(sent(sidecar, "cleanup.undo")).toHaveLength(1);
  });

  it("says a new file is deleted, not discarded, and keeps the tracked files", async () => {
    setup();
    await ready();

    fireEvent.click(discardButton("docs/notes.md")!);

    expect(within(await dialog()).getByText("Delete the new file docs/notes.md")).toBeTruthy();
  });

  it("keeps what is staged when a partly staged file is discarded", async () => {
    setup();
    await ready();
    fireEvent.click(within(lists()).getByRole("button", { name: "Stage src/Cart.cs" }));
    await waitFor(() => expect(paths("Staged")).toContain("src/Cart.cs"));

    expect(discardButton("src/Cart.cs")).toBeNull();
    expect(paths("Staged")).toEqual(["README.md", "src/Cart.cs"]);
  });

  it("discards one change from the button in the diff, and the other stays", async () => {
    const sidecar = setup();
    await ready();
    await waitFor(() => expect(monaco.actions.filter((a) => a.label === "Discard hunk")).toHaveLength(2));

    act(() => monaco.actions.filter((a) => a.label === "Discard hunk")[1]!.run());
    const confirm = await dialog();

    expect(sent(sidecar, "cleanup.planDiscard")).toEqual([{ repoId: sampleState.repoId, worktree: mainWorktree, hunk: { path: "src/Cart.cs", oldPath: null, id: "cart-round" } }]);
    expect(within(confirm).getByText(/^Discard the change at line 9/)).toBeTruthy();
    fireEvent.click(within(confirm).getByRole("button", { name: "Discard" }));

    await waitFor(() => expect(monaco.actions.filter((a) => a.label === "Discard hunk")).toHaveLength(1));
    expect(paths("Unstaged")).toContain("src/Cart.cs");
  });

  it("discards the change shown in the bar above the diff", async () => {
    const sidecar = setup();
    await ready();
    await waitFor(() => expect(monaco.actions).not.toHaveLength(0));

    fireEvent.click(within(screen.getByRole("group", { name: "Changes in this file" })).getByRole("button", { name: "Discard this change" }));
    await dialog();

    expect(sent(sidecar, "cleanup.planDiscard")).toEqual([{ repoId: sampleState.repoId, worktree: mainWorktree, hunk: { path: "src/Cart.cs", oldPath: null, id: "cart-total" } }]);
  });

  it("offers no Discard hunk on a staged change", async () => {
    setup();
    await ready();
    fireEvent.click(within(lists()).getByTitle("README.md"));

    await waitFor(() => expect(monaco.actions.map((a) => a.label)).toEqual(["Unstage hunk"]));
    expect(within(screen.getByRole("group", { name: "Changes in this file" })).queryByRole("button", { name: /^Discard/ })).toBeNull();
  });

  it("says why when the change cannot be planned, and throws nothing away", async () => {
    const sidecar = new FakeSidecar();
    sidecar.failures.set("cleanup.planDiscard", {
      error: { code: -32602, message: "src/Cart.cs has no unstaged changes to discard. Refresh to see the changes as they are now.", data: { kind: "InvalidParams" } },
    });
    setup(sidecar);
    await ready();

    fireEvent.click(discardButton("src/Cart.cs")!);

    expect((await screen.findByRole("alert")).textContent).toContain("has no unstaged changes to discard");
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(sent(sidecar, "cleanup.apply")).toEqual([]);
  });
});
