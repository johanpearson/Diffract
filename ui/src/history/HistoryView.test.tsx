import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { RpcClient } from "../rpc";
import { FakeSidecar, sampleCommits, sampleDetails, sampleState } from "../testing/fakeSidecar";
import { HistoryView } from "./HistoryView";

// jsdom has no canvas. The graph's drawing has its own tests.
beforeEach(() => void vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null));
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const [merge, root] = sampleCommits as [(typeof sampleCommits)[0], (typeof sampleCommits)[0]];

function setup(sidecar = new FakeSidecar()) {
  render(<HistoryView client={new RpcClient(sidecar.transport)} state={sampleState} now={new Date("2026-10-21T09:30:00+02:00")} />);
  return sidecar;
}

const list = () => screen.getByRole("listbox", { name: "Commits" });
const options = () => within(list()).getAllByRole("option");
const selected = () => within(list()).getByRole("option", { selected: true });
const panel = () => screen.getByRole("complementary", { name: "Commit details" });

async function loaded(count: string) {
  await waitFor(() => expect(screen.getByRole("status").textContent).toBe(count));
}

describe("HistoryView", () => {
  it("lists every commit with its refs, author and age", async () => {
    setup();
    await loaded("2 commits");

    const [first, second] = options();
    expect(first!.textContent).toContain("main");
    expect(first!.textContent).toContain("Merge branch 'claude/räksmörgås-fix'");
    expect(first!.textContent).toContain("Åsa Öberg");
    expect(first!.textContent).toContain("3 h ago");
    expect(second!.textContent).toContain("origin/main");
    expect(second!.textContent).toContain("v1.0");
    expect(second!.textContent).toContain("(no subject)");
  });

  it("selects the newest commit and shows its details", async () => {
    setup();

    expect(await within(panel()).findByRole("heading", { name: sampleDetails.subject })).toBeTruthy();
    expect(selected().textContent).toContain(merge.subject);
    expect(panel().textContent).toContain("Co-authored-by: Claude");
    expect(panel().textContent).toContain("GitHub");
    const files = within(panel()).getByRole("list", { name: "Changed files" });
    expect(within(files).getAllByRole("listitem").map((li) => li.textContent)).toEqual(["Rsrc/räksmörgås.cs+3 −1", "Adocs/logo.pngbinary"]);
  });

  it("moves the selection with the keyboard and follows it with the details", async () => {
    const sidecar = setup();
    await loaded("2 commits");

    fireEvent.keyDown(list(), { key: "ArrowDown" });

    expect(selected().textContent).toContain("(no subject)");
    expect(await within(panel()).findByText("None, this is a root commit")).toBeTruthy();
    expect(sidecar.detailsAsked.at(-1)).toBe(root.id);
    fireEvent.keyDown(list(), { key: "Home" });
    expect(selected().textContent).toContain(merge.subject);
  });

  it("selects a commit by clicking it", async () => {
    setup();
    await loaded("2 commits");

    fireEvent.click(options()[1]!);

    expect(selected().textContent).toContain("(no subject)");
  });

  it("goes to a parent from the details, and cannot go to one that is not loaded", async () => {
    setup();
    const parents = await within(panel()).findAllByRole("button");

    expect(parents.map((b) => [b.textContent, (b as HTMLButtonElement).disabled])).toEqual([
      [root.id.slice(0, 8), false],
      [merge.parentIds[1]!.slice(0, 8), true],
    ]);
    fireEvent.click(parents[0]!);
    expect(selected().textContent).toContain("(no subject)");
  });

  it("moves to the details with Enter", async () => {
    setup();
    await loaded("2 commits");

    fireEvent.keyDown(list(), { key: "Enter" });

    expect(document.activeElement).toBe(panel());
  });

  it("draws only the rows in view of a long history", async () => {
    const sidecar = new FakeSidecar();
    sidecar.useGeneratedHistory(5_000);
    setup(sidecar);
    await loaded("5,000 commits");

    expect(options().length).toBeLessThan(60);
    fireEvent.keyDown(list(), { key: "End" });
    await waitFor(() => expect(selected().getAttribute("aria-posinset")).toBe("5000"));
    expect(selected().getAttribute("aria-setsize")).toBe("5000");
  });

  it("asks for details only once the selection rests", async () => {
    const sidecar = new FakeSidecar();
    sidecar.useGeneratedHistory(100);
    setup(sidecar);
    await loaded("100 commits");
    await waitFor(() => expect(sidecar.detailsAsked).toHaveLength(1));

    for (let i = 0; i < 10; i++) fireEvent.keyDown(list(), { key: "ArrowDown" });

    await waitFor(() => expect(sidecar.detailsAsked).toHaveLength(2));
    expect(sidecar.detailsAsked[1]).toBe(sidecar.history[10]!.id);
  });

  it("says so when the history cannot be read", async () => {
    const sidecar = new FakeSidecar();
    sidecar.failures.set("history.read", { error: { code: -32002, message: "git log failed", data: { kind: "GitFailed" } } });
    setup(sidecar);

    expect((await screen.findByRole("alert")).textContent).toContain("git log failed");
  });
});
