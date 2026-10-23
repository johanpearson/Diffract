import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ChangeAction, FileSides } from "../history/monacoEditor";
import { RpcClient } from "../rpc";
import { FakeSidecar, sampleState } from "../testing/fakeSidecar";
import { ChangesView } from "./ChangesView";

// jsdom runs no Monaco. This stand-in records what the view shows and the change buttons it was given.
const monaco = vi.hoisted(() => ({ shown: [] as FileSides[], actions: [] as readonly ChangeAction[], revealed: [] as number[] }));
vi.mock("../history/monacoEditor", () => ({
  createFileView: () => ({
    show: (sides: FileSides) => void monaco.shown.push(sides),
    setLayout: () => {},
    setDark: () => {},
    goToChange: () => {},
    setActions: (actions: readonly ChangeAction[]) => void (monaco.actions = actions),
    revealLine: (line: number) => void monaco.revealed.push(line),
    dispose: () => {},
  }),
}));

beforeEach(() => {
  monaco.shown = [];
  monaco.actions = [];
  monaco.revealed = [];
});
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const mainWorktree = "C:\\src\\shop";
const agentWorktree = "C:\\src\\shop\\.claude\\worktrees\\räksmörgås fix";
const storage = { get: () => null, set: () => {} };

function setup(sidecar = new FakeSidecar(), openedPath = mainWorktree) {
  render(<ChangesView client={new RpcClient(sidecar.transport)} state={sampleState} openedPath={openedPath} storage={storage} />);
  return sidecar;
}

const lists = () => screen.getByRole("listbox", { name: "Changed files" });
const group = (name: "Unstaged" | "Staged") => within(lists()).getByRole("group", { name: new RegExp(`^${name}`) });
const rows = (name: "Unstaged" | "Staged") => within(group(name)).queryAllByRole("option").map((o) => o.textContent);
const selected = () => within(lists()).getByRole("option", { selected: true });
const commitButton = () => screen.getByRole("button", { name: /^(Commit|Amend)/ });

async function ready() {
  await screen.findByRole("listbox", { name: "Changed files" });
}

describe("the Changes view", () => {
  it("lists unstaged and staged files, and hides the agent's own worktree inside the repo", async () => {
    setup();
    await ready();

    expect(rows("Unstaged")).toEqual(["Msrc/Cart.csStage", "Adocs/notes.mdStage"]);
    expect(rows("Staged")).toEqual(["MREADME.mdUnstage"]);
    expect(lists().textContent).not.toContain(".claude");
    expect(screen.getByRole("status").textContent).toBe("1 ahead origin/main");
  });

  it("shows the first file's diff with a Stage hunk button above each change", async () => {
    setup();
    await ready();

    await waitFor(() => expect(monaco.shown.at(-1)?.path).toBe("src/Cart.cs"));
    expect(monaco.shown.at(-1)!.modified).toContain("Math.Round");
    await waitFor(() => expect(monaco.actions.map((a) => [a.line, a.label])).toEqual([[5, "Stage hunk"], [9, "Stage hunk"]]));
    expect(screen.getByRole("region", { name: "Diff of src/Cart.cs" }).textContent).toContain("Not staged, against the index");
  });

  it("stages one hunk, and the file is then in both lists", async () => {
    const sidecar = setup();
    await ready();
    await waitFor(() => expect(monaco.actions).toHaveLength(2));

    act(() => monaco.actions[1]!.run());

    await waitFor(() => expect(rows("Staged")).toEqual(["MREADME.mdUnstage", "Msrc/Cart.csUnstage"]));
    expect(rows("Unstaged")[0]).toBe("Msrc/Cart.csStage");
    expect(sidecar.stagesAsked.at(-1)).toEqual({ method: "changes.stage", paths: undefined, hunk: { path: "src/Cart.cs", oldPath: null, id: "cart-round" } });
    await waitFor(() => expect(monaco.actions).toHaveLength(1));
  });

  it("moves through the changes from the bar above the diff, scrolls to each, and stages the one shown", async () => {
    const sidecar = setup();
    await ready();
    await waitFor(() => expect(monaco.actions).toHaveLength(2));
    const bar = screen.getByRole("group", { name: "Changes in this file" });
    expect(bar.textContent).toContain("Change 1 of 2, line 5");
    expect((within(bar).getByRole("button", { name: "Previous change" }) as HTMLButtonElement).disabled).toBe(true);

    fireEvent.click(within(bar).getByRole("button", { name: "Next change" }));
    expect(bar.textContent).toContain("Change 2 of 2, line 9");
    expect(monaco.revealed).toEqual([9]);
    fireEvent.click(within(bar).getByRole("button", { name: "Stage this change" }));

    await waitFor(() => expect(sidecar.stagesAsked.at(-1)?.hunk).toMatchObject({ id: "cart-round" }));
  });

  it("stages and unstages a file with its button", async () => {
    setup();
    await ready();

    fireEvent.click(screen.getByRole("button", { name: "Stage docs/notes.md" }));
    await waitFor(() => expect(rows("Staged")).toEqual(["MREADME.mdUnstage", "Adocs/notes.mdUnstage"]));

    fireEvent.click(screen.getByRole("button", { name: "Unstage README.md" }));
    await waitFor(() => expect(rows("Staged")).toEqual(["Adocs/notes.mdUnstage"]));
    expect(rows("Unstaged")).toEqual(["MREADME.mdStage", "Msrc/Cart.csStage"]);
  });

  it("stages everything with Stage all, in one call", async () => {
    const sidecar = setup();
    await ready();

    fireEvent.click(within(group("Unstaged")).getByRole("button", { name: "Stage all" }));

    await waitFor(() => expect(rows("Unstaged")).toEqual([]));
    expect(sidecar.stagesAsked).toEqual([{ method: "changes.stage", paths: ["src/Cart.cs", "docs/notes.md"], hunk: undefined }]);
    expect(group("Unstaged").textContent).toContain("Nothing to stage");
  });

  it("moves through both lists with the arrow keys and stages the selected file with Space", async () => {
    setup();
    await ready();
    expect(selected().textContent).toContain("src/Cart.cs");

    fireEvent.keyDown(lists(), { key: "ArrowDown" });
    expect(selected().textContent).toContain("docs/notes.md");
    fireEvent.keyDown(lists(), { key: "ArrowDown" });
    expect(selected().textContent).toContain("README.md");
    fireEvent.keyDown(lists(), { key: "ArrowUp" });
    fireEvent.keyDown(lists(), { key: " " });

    await waitFor(() => expect(rows("Staged")).toContain("Adocs/notes.mdUnstage"));
    // The selection follows the file to the list it went to.
    expect(selected().textContent).toContain("docs/notes.md");
  });

  it("commits when something is staged and the message has a subject, then clears the box", async () => {
    const sidecar = setup();
    await ready();
    const message = screen.getByRole("textbox", { name: "Commit message" });
    expect(commitButton().textContent).toBe("Commit 1 file");
    expect((commitButton() as HTMLButtonElement).disabled).toBe(true);

    fireEvent.change(message, { target: { value: "\nOnly a body" } });
    expect((commitButton() as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(message, { target: { value: "Explain the shop\n\nIn the README." } });
    fireEvent.click(commitButton());

    await waitFor(() => expect(rows("Staged")).toEqual([]));
    expect(sidecar.worktree(mainWorktree).commits).toEqual([{ message: "Explain the shop\n\nIn the README.", amend: false }]);
    expect((message as HTMLTextAreaElement).value).toBe("");
    expect(screen.getByRole("status").textContent).toBe("2 ahead origin/main");
  });

  it("commits with Ctrl+Enter wherever the focus is", async () => {
    const sidecar = setup();
    await ready();
    fireEvent.change(screen.getByRole("textbox", { name: "Commit message" }), { target: { value: "Keyboard commit" } });

    fireEvent.keyDown(lists(), { key: "Enter", ctrlKey: true });

    await waitFor(() => expect(sidecar.worktree(mainWorktree).commits).toHaveLength(1));
  });

  it("shows why git refused a commit, and keeps the message", async () => {
    const sidecar = setup();
    sidecar.failures.set("changes.commit", {
      error: { code: -32008, message: "Git did not commit. A hook or commit signing can stop a commit. This is what git said:\n\nlint failed", data: { kind: "CommitRefused" } },
    });
    await ready();
    const message = screen.getByRole("textbox", { name: "Commit message" });
    fireEvent.change(message, { target: { value: "Blocked by a hook" } });

    fireEvent.click(commitButton());

    expect((await screen.findByRole("alert")).textContent).toContain("lint failed");
    expect((message as HTMLTextAreaElement).value).toBe("Blocked by a hook");
    expect(rows("Staged")).toEqual(["MREADME.mdUnstage"]);
  });

  it("amends with nothing staged, and turns amend off with the reason when the last commit is pushed", async () => {
    const sidecar = setup();
    await ready();
    fireEvent.click(screen.getByRole("button", { name: "Unstage README.md" }));
    await waitFor(() => expect(rows("Staged")).toEqual([]));
    const amend = screen.getByRole("checkbox", { name: "Amend last commit" }) as HTMLInputElement;
    fireEvent.change(screen.getByRole("textbox", { name: "Commit message" }), { target: { value: "Better words" } });
    expect((commitButton() as HTMLButtonElement).disabled).toBe(true);

    fireEvent.click(amend);
    fireEvent.click(commitButton());

    await waitFor(() => expect(sidecar.worktree(mainWorktree).commits).toEqual([{ message: "Better words", amend: true }]));
    act(() => sidecar.worktree(mainWorktree).status = { ...sidecar.worktree(mainWorktree).status, headOnRemote: "origin/main" });
    act(() => sidecar.change(sampleState.repoId, (s) => s, [{ kind: "WorktreeHead", path: mainWorktree }]));
    await waitFor(() => expect(amend.disabled).toBe(true));
    expect(screen.getByText("The last commit is on origin/main, so amending it would rewrite pushed work.")).toBeTruthy();
  });

  it("reads the changes again when the sidecar says this worktree changed", async () => {
    const sidecar = setup();
    await ready();

    const wt = sidecar.worktree(mainWorktree);
    act(() => {
      wt.status = { ...wt.status, files: [...wt.status.files, { path: "new.txt", oldPath: null, kind: "Untracked", staged: null, unstaged: "Added", headId: null, indexId: null }] };
      sidecar.change(sampleState.repoId, (s) => s, [{ kind: "WorktreeFiles", path: mainWorktree }]);
    });

    await waitFor(() => expect(rows("Unstaged")).toContain("Anew.txtStage"));
  });

  it("ignores changes to another worktree", async () => {
    const sidecar = setup();
    await ready();
    const asked = () => sidecar.transport.sent.filter((m) => m.method === "changes.status").length;
    const before = asked();

    act(() => sidecar.change(sampleState.repoId, (s) => s, [{ kind: "WorktreeFiles", path: agentWorktree }]));

    await new Promise((r) => setTimeout(r, 20));
    expect(asked()).toBe(before);
  });

  it("warns before anything is staged in a worktree an agent is using, until the user accepts", async () => {
    setup(new FakeSidecar(), agentWorktree);
    await ready();

    const warning = screen.getByRole("alert");
    expect(warning.textContent).toContain("An agent may be working in this worktree");
    expect((screen.getByRole("button", { name: "Stage src/räksmörgås.cs" }) as HTMLButtonElement).disabled).toBe(true);

    fireEvent.click(within(warning).getByRole("button", { name: "Work here anyway" }));

    expect((screen.getByRole("button", { name: "Stage src/räksmörgås.cs" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("switches to another worktree with the picker", async () => {
    setup();
    await ready();

    fireEvent.change(screen.getByRole("combobox", { name: "Worktree" }), { target: { value: agentWorktree } });

    await waitFor(() => expect(rows("Unstaged")).toEqual(["Msrc/räksmörgås.csStage"]));
    expect(screen.getByText("claude/räksmörgås-fix")).toBeTruthy();
  });

  it("says a hunk changed since it was shown, and reads the file again", async () => {
    const sidecar = setup();
    await ready();
    await waitFor(() => expect(monaco.actions).toHaveLength(2));
    sidecar.failures.set("changes.stage", {
      error: { code: -32007, message: "src/Cart.cs changed since it was shown, so nothing was staged or unstaged. Look at it again.", data: { kind: "ChangedSinceShown" } },
    });

    act(() => monaco.actions[0]!.run());

    expect((await screen.findByRole("alert")).textContent).toContain("changed since it was shown");
  });

  it("lists a conflicted file without a Stage button", async () => {
    const sidecar = new FakeSidecar();
    const wt = sidecar.worktree(mainWorktree);
    wt.status = { ...wt.status, files: [{ path: "src/merge.cs", oldPath: null, kind: "Conflicted", staged: null, unstaged: null, headId: null, indexId: null }] };
    setup(sidecar);
    await ready();

    expect(rows("Unstaged")).toEqual(["Usrc/merge.csConflict"]);
    expect(within(group("Unstaged")).queryByRole("button", { name: /Stage src/ })).toBeNull();
    expect(await screen.findByText(/merge conflict/)).toBeTruthy();
  });
});
