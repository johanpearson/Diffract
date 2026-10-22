import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { FileChange } from "../protocol";
import { RpcClient } from "../rpc";
import { FakeSidecar, sampleDetails, sampleState } from "../testing/fakeSidecar";
import { HistoryView } from "./HistoryView";
import { DiffPane } from "./DiffPane";
import type { DiffSides } from "./monacoEditor";

// jsdom runs neither Monaco nor a canvas. This stand-in records what the diff editor was asked to show.
const monaco = vi.hoisted(() => ({ loads: 0, shown: [] as DiffSides[], disposed: 0 }));
vi.mock("./monacoEditor", () => {
  monaco.loads++;
  return {
    createDiffEditor: () => ({ show: (sides: DiffSides) => monaco.shown.push(sides), setDark: () => {}, dispose: () => monaco.disposed++ }),
  };
});

beforeEach(() => {
  vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null);
  monaco.shown = [];
});
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const [renamed] = sampleDetails.files as [FileChange];
const panel = () => screen.getByRole("complementary", { name: "Commit details" });
const list = () => screen.getByRole("listbox", { name: "Commits" });

function setup(sidecar = new FakeSidecar()) {
  const client = new RpcClient(sidecar.transport);
  render(<HistoryView client={client} state={sampleState} now={new Date("2026-10-21T09:30:00+02:00")} />);
  return { sidecar, client };
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

  it("shows an added file against an empty left side, asking only for the new side", async () => {
    const sidecar = new FakeSidecar();
    const added: FileChange = { ...renamed, kind: "Added", oldPath: null, oldId: null, similarity: null };
    render(<DiffPane client={new RpcClient(sidecar.transport)} repoId="1" file={added} onClose={() => {}} />);

    await waitFor(() => expect(monaco.shown).toHaveLength(1));
    expect(sidecar.filesAsked).toEqual([added.newId]);
    expect(monaco.shown[0]!.original).toBe("");
  });

  it("says a submodule has no text to compare", () => {
    const submodule: FileChange = { ...renamed, isSubmodule: true };
    render(<DiffPane client={new RpcClient(new FakeSidecar().transport)} repoId="1" file={submodule} onClose={() => {}} />);

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
