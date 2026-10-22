import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { FileChange } from "../protocol";
import { RpcClient } from "../rpc";
import { FakeSidecar, sampleDetails, sampleState } from "../testing/fakeSidecar";
import { HistoryView } from "./HistoryView";
import { DiffLayoutKey, DiffPane } from "./DiffPane";
import type { DiffLayout, FileSides } from "./monacoEditor";

// jsdom runs neither Monaco nor a canvas. This stand-in records what the view was asked to do.
const monaco = vi.hoisted(() => ({ loads: 0, shown: [] as FileSides[], layouts: [] as string[], moves: [] as string[] }));
vi.mock("./monacoEditor", () => {
  monaco.loads++;
  return {
    createFileView: (_container: HTMLElement, _dark: boolean, layout: DiffLayout) => {
      monaco.layouts.push(`created ${layout}`);
      return {
        show: (sides: FileSides) => void monaco.shown.push(sides),
        setLayout: (value: DiffLayout) => void monaco.layouts.push(value),
        setDark: () => {},
        goToChange: (target: string) => void monaco.moves.push(target),
        dispose: () => {},
      };
    },
  };
});

beforeEach(() => {
  vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null);
  monaco.shown = [];
  monaco.layouts = [];
  monaco.moves = [];
});

/** Storage for one test, as the host gives it. */
function memory(entries: Record<string, string> = {}) {
  const map = new Map(Object.entries(entries));
  return { map, get: (key: string) => map.get(key) ?? null, set: (key: string, value: string) => void map.set(key, value) };
}
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const [renamed] = sampleDetails.files as [FileChange];
const panel = () => screen.getByRole("complementary", { name: "Commit details" });
const list = () => screen.getByRole("listbox", { name: "Commits" });

function setup(sidecar = new FakeSidecar(), storage = memory()) {
  const client = new RpcClient(sidecar.transport);
  render(<HistoryView client={client} state={sampleState} now={new Date("2026-10-21T09:30:00+02:00")} storage={storage} />);
  return { sidecar, client, storage };
}

async function openFile(path: string) {
  fireEvent.click(await within(panel()).findByRole("button", { name: new RegExp(path) }));
}

describe("the diff of a commit's file", () => {
  it("loads Monaco only when the first diff opens, and shows both sides of the file", async () => {
    const { sidecar } = setup();
    await within(panel()).findByRole("list", { name: "Changed files" });
    expect(monaco.loads).toBe(0);

    await openFile("räksmörgås.cs");

    await waitFor(() => expect(monaco.shown).toHaveLength(1));
    expect(monaco.loads).toBe(1);
    expect(sidecar.filesAsked).toEqual([renamed.oldId, renamed.newId]);
    expect(monaco.shown[0]).toMatchObject({ path: "src/räksmörgås.cs" });
    expect(monaco.shown[0]!.original).toContain("public class Cart");
    expect(monaco.shown[0]!.modified).toContain("public class Räksmörgås");
    expect(screen.getByRole("region", { name: "Diff of src/räksmörgås.cs" }).textContent).toContain("src/cart.cs → src/räksmörgås.cs");
    expect(within(panel()).getByRole("button", { name: /räksmörgås.cs/ }).getAttribute("aria-pressed")).toBe("true");
  });

  it("says a binary file has no text, without asking the sidecar for it", async () => {
    const { sidecar } = setup();

    await openFile("logo.png");

    expect(screen.getByRole("region", { name: "Diff of docs/logo.png" }).textContent).toContain("binary file");
    expect(sidecar.filesAsked).toEqual([]);
  });

  it("says why when the sidecar sends no text for a side", async () => {
    const sidecar = new FakeSidecar();
    sidecar.files.set(renamed.newId!, { kind: "TooLarge", text: null, message: "This file is larger than 5 MB." });
    setup(sidecar);

    await openFile("räksmörgås.cs");

    expect(await screen.findByText("This file is larger than 5 MB.")).toBeTruthy();
  });

  it("closes with the Close button, with Escape, and when another commit is selected", async () => {
    setup();
    const region = () => screen.queryByRole("region", { name: /^Diff of/ });

    await openFile("logo.png");
    fireEvent.click(within(region()!).getByRole("button", { name: "Close" }));
    expect(region()).toBeNull();

    await openFile("logo.png");
    fireEvent.keyDown(within(region()!).getByRole("button", { name: "Close" }), { key: "Escape" });
    expect(region()).toBeNull();

    await openFile("logo.png");
    fireEvent.keyDown(list(), { key: "ArrowDown" });
    expect(region()).toBeNull();
  });

  it("shows an added file on its own, asking only for the new side, with no layout to pick", async () => {
    const sidecar = new FakeSidecar();
    const added: FileChange = { ...renamed, kind: "Added", oldPath: null, oldId: null, similarity: null };
    render(<DiffPane client={new RpcClient(sidecar.transport)} repoId="1" file={added} storage={memory()} onClose={() => {}} />);

    await waitFor(() => expect(monaco.shown).toHaveLength(1));
    expect(sidecar.filesAsked).toEqual([added.newId]);
    expect(monaco.shown[0]).toMatchObject({ original: null, modified: expect.stringContaining("Räksmörgås") });
    expect(screen.queryByRole("group", { name: "Diff layout" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Next change" })).toBeNull();
  });

  it("shows a deleted file on its own, from the old side", async () => {
    const sidecar = new FakeSidecar();
    const deleted: FileChange = { ...renamed, kind: "Deleted", oldPath: null, newId: null, similarity: null };
    render(<DiffPane client={new RpcClient(sidecar.transport)} repoId="1" file={deleted} storage={memory()} onClose={() => {}} />);

    await waitFor(() => expect(monaco.shown).toHaveLength(1));
    expect(sidecar.filesAsked).toEqual([deleted.oldId]);
    expect(monaco.shown[0]).toMatchObject({ original: expect.stringContaining("Cart"), modified: null });
  });

  it("switches between side by side and inline, and remembers the choice", async () => {
    const storage = memory();
    render(<DiffPane client={new RpcClient(new FakeSidecar().transport)} repoId="1" file={renamed} storage={storage} onClose={() => {}} />);
    await waitFor(() => expect(monaco.shown).toHaveLength(1));
    const layout = screen.getByRole("group", { name: "Diff layout" });
    expect(within(layout).getByRole("button", { name: "Side by side" }).getAttribute("aria-pressed")).toBe("true");

    fireEvent.click(within(layout).getByRole("button", { name: "Inline" }));

    expect(monaco.layouts.at(-1)).toBe("inline");
    expect(storage.map.get(DiffLayoutKey)).toBe("inline");
    expect(within(layout).getByRole("button", { name: "Inline" }).getAttribute("aria-pressed")).toBe("true");
  });

  it("opens in the layout chosen last time", async () => {
    render(<DiffPane client={new RpcClient(new FakeSidecar().transport)} repoId="1" file={renamed} storage={memory({ [DiffLayoutKey]: "inline" })} onClose={() => {}} />);

    await waitFor(() => expect(monaco.layouts[0]).toBe("created inline"));
  });

  it("moves to the next and previous change", async () => {
    render(<DiffPane client={new RpcClient(new FakeSidecar().transport)} repoId="1" file={renamed} storage={memory()} onClose={() => {}} />);
    const next = screen.getByRole("button", { name: "Next change" });
    await waitFor(() => expect((next as HTMLButtonElement).disabled).toBe(false));

    fireEvent.click(next);
    fireEvent.click(screen.getByRole("button", { name: "Previous change" }));

    expect(monaco.moves).toEqual(["next", "previous"]);
  });

  it("says a submodule has no text to compare", () => {
    const submodule: FileChange = { ...renamed, isSubmodule: true };
    render(<DiffPane client={new RpcClient(new FakeSidecar().transport)} repoId="1" file={submodule} storage={memory()} onClose={() => {}} />);

    expect(screen.getByRole("region").textContent).toContain("This is a submodule.");
  });
});

describe("the find box", () => {
  async function setupLong() {
    const sidecar = new FakeSidecar();
    sidecar.useGeneratedHistory(100);
    setup(sidecar);
    await waitFor(() => expect(screen.getByRole("status").textContent).toBe("100 commits"));
    return sidecar;
  }
  const find = () => screen.getByRole("searchbox", { name: "Find commits" });
  const selected = () => within(list()).getByRole("option", { selected: true });

  it("goes to the next match with Enter and back with Shift+Enter, wrapping around", async () => {
    const sidecar = await setupLong();
    const matches = sidecar.history.flatMap((c, i) => (c.subject.includes("on feature 2") ? [i + 1] : []));

    fireEvent.change(find(), { target: { value: "on feature 2" } });
    expect(screen.getByText(`${matches.length} found`)).toBeTruthy();
    fireEvent.keyDown(find(), { key: "Enter" });
    expect(selected().getAttribute("aria-posinset")).toBe(String(matches[0]));
    expect(screen.getByText(`1 of ${matches.length}`)).toBeTruthy();
    fireEvent.keyDown(find(), { key: "Enter" });
    expect(selected().getAttribute("aria-posinset")).toBe(String(matches[1]));
    fireEvent.keyDown(find(), { key: "Enter", shiftKey: true });
    fireEvent.keyDown(find(), { key: "Enter", shiftKey: true });
    expect(selected().getAttribute("aria-posinset")).toBe(String(matches.at(-1)));
  });

  it("finds a commit by the start of its hash", async () => {
    const sidecar = await setupLong();
    const target = sidecar.history[42]!;

    fireEvent.change(find(), { target: { value: target.id.slice(0, 7) } });
    fireEvent.keyDown(find(), { key: "Enter" });

    expect(selected().getAttribute("aria-posinset")).toBe("43");
  });

  it("says when nothing matches, and Escape clears the box", async () => {
    await setupLong();

    fireEvent.change(find(), { target: { value: "no such commit" } });
    expect(screen.getByText("No matches")).toBeTruthy();
    fireEvent.keyDown(find(), { key: "Enter" });
    expect(selected().getAttribute("aria-posinset")).toBe("1");
    fireEvent.keyDown(find(), { key: "Escape" });
    expect((find() as HTMLInputElement).value).toBe("");
  });

  it("is focused with Ctrl+F from anywhere in the history", async () => {
    await setupLong();

    fireEvent.keyDown(list(), { key: "f", ctrlKey: true });

    expect(document.activeElement).toBe(find());
  });
});
