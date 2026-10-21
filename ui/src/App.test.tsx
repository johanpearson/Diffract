import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App, LastRepoKey } from "./App";
import type { AppHost, SidecarEvent } from "./host";
import { RpcClient } from "./rpc";
import { FakeSidecar, sampleState } from "./testing/fakeSidecar";

afterEach(cleanup);

const repoRoot = sampleState.repoRoot;

/** Renders the app against a fake sidecar. `last` is the repo remembered from the previous run. */
function setup({ last = null as string | null, sidecar = new FakeSidecar(), folder = repoRoot as string | null } = {}) {
  const stored = new Map<string, string>(last ? [[LastRepoKey, last]] : []);
  const handlers = new Set<(event: SidecarEvent) => void>();
  const host: AppHost = {
    transport: sidecar.transport,
    pickFolder: vi.fn(async () => folder),
    onSidecarEvent: (handler) => (handlers.add(handler), () => handlers.delete(handler)),
    restartSidecar: vi.fn(async () => {}),
    storage: { get: (key) => stored.get(key) ?? null, set: (key, value) => void stored.set(key, value) },
  };
  render(<App host={host} client={new RpcClient(sidecar.transport)} clock={() => new Date("2026-10-21T09:30:00+02:00")} />);
  const sidecarEvent = (event: SidecarEvent) => act(() => handlers.forEach((handler) => handler(event)));
  return { sidecar, host, stored, sidecarEvent };
}

const leftoverList = () => screen.getByRole("list", { name: "Leftovers" });
const row = (name: string) => within(leftoverList()).getByRole("listitem", { name });
const rowNames = () => within(leftoverList()).getAllByRole("listitem").map((li) => li.getAttribute("aria-label"));
const sent = (sidecar: FakeSidecar, method: string) => sidecar.transport.sent.filter((m) => m.method === method);

describe("App", () => {
  it("asks for a repo on the first start, and shows the versions", async () => {
    setup();

    expect(await screen.findByRole("heading", { name: "Open a repo" })).toBeTruthy();
    expect(screen.getByText("git 2.47.1 · sidecar 0.1.0")).toBeTruthy();
  });

  it("opens the last repo again on start", async () => {
    const { sidecar } = setup({ last: repoRoot });

    expect(await screen.findByRole("heading", { name: "Worktrees and agents" })).toBeTruthy();
    expect(rowNames()).toEqual(["old-task", "copilot/fix-login", "räksmörgås fix"]);
    expect(sent(sidecar, "repo.open")[0]?.params).toEqual({ path: repoRoot });
  });

  it("opens a repo picked in the folder dialog and remembers it", async () => {
    const { stored } = setup();
    await screen.findByRole("heading", { name: "Open a repo" });

    fireEvent.click(screen.getAllByRole("button", { name: "Open repo…" })[0]!);

    await screen.findByRole("list", { name: "Leftovers" });
    expect(stored.get(LastRepoKey)).toBe(repoRoot);
  });

  it("does nothing when the folder dialog is cancelled", async () => {
    const { sidecar } = setup({ folder: null });
    await screen.findByRole("heading", { name: "Open a repo" });

    fireEvent.click(screen.getAllByRole("button", { name: "Open repo…" })[0]!);

    await waitFor(() => expect(screen.getByRole("heading", { name: "Open a repo" })).toBeTruthy());
    expect(sent(sidecar, "repo.open")).toEqual([]);
  });

  it("shows the counts, the worktrees and the leftovers count in the sidebar", async () => {
    setup({ last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });

    const sums = [...document.querySelectorAll(".sum")].map((s) => s.textContent);
    expect(sums).toEqual(["1Safe to remove", "1Needs review", "1In use"]);
    const sidebar = screen.getByRole("navigation", { name: "Repository" });
    expect(within(sidebar).getByText("claude/räksmörgås-fix")).toBeTruthy();
    expect(within(sidebar).getByText("3 changed")).toBeTruthy();
    expect(within(sidebar).getByText("locked")).toBeTruthy();
    expect(within(sidebar).getByLabelText("2 not in use")).toBeTruthy();
  });

  it("shows each row's kind, agent, reason, path, commits at risk and verdict", async () => {
    setup({ last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });

    const folder = row("old-task");
    expect(within(folder).getByText("Folder, Claude Code")).toBeTruthy();
    expect(within(folder).getByText("At risk: -")).toBeTruthy();
    expect(within(folder).getByText("Safe to remove")).toBeTruthy();
    const branch = row("copilot/fix-login");
    expect(within(branch).getByText("At risk: 2")).toBeTruthy();
    expect(within(branch).queryByRole("button", { name: /Open folder/ })).toBeNull();
    expect(within(row("räksmörgås fix")).getByText("C:\\src\\shop\\.claude\\worktrees\\räksmörgås fix")).toBeTruthy();
  });

  it("filters by verdict", async () => {
    setup({ last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });

    fireEvent.click(screen.getByRole("button", { name: "In use" }));

    expect(rowNames()).toEqual(["räksmörgås fix"]);
    expect(screen.getByRole("button", { name: "In use" }).getAttribute("aria-pressed")).toBe("true");
    expect(screen.getByRole("button", { name: "All" }).getAttribute("aria-pressed")).toBe("false");
  });

  it("updates rows live, highlights them for a moment, and lets removed ones fade out", async () => {
    const { sidecar } = setup({ last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });

    act(() =>
      sidecar.change("1", (s) => ({
        ...s,
        takenAt: "2026-10-21T09:31:00+02:00",
        leftovers: [{ ...s.leftovers[1]!, verdict: "SafeToRemove" }, s.leftovers[2]!],
        summary: { total: 2, safeToRemove: 1, needsReview: 0, inUse: 1 },
      })),
    );

    expect(row("copilot/fix-login").getAttribute("data-flash")).toBe("changed");
    expect(row("old-task").getAttribute("data-flash")).toBe("removed");
    expect(within(row("copilot/fix-login")).getByText("Safe to remove")).toBeTruthy();
    await waitFor(() => expect(rowNames()).toEqual(["copilot/fix-login", "räksmörgås fix"]), { timeout: 3000 });
    expect(row("copilot/fix-login").getAttribute("data-flash")).toBeNull();
  });

  it("ignores changes to a repo that is no longer on screen", async () => {
    const { sidecar } = setup({ last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });

    act(() =>
      sidecar.transport.emit(JSON.stringify({ jsonrpc: "2.0", method: "repo.changed", params: { changes: [], state: { ...sampleState, repoId: "9", leftovers: [] } } })),
    );

    expect(rowNames()).toHaveLength(3);
  });

  it("opens a folder through the sidecar", async () => {
    const { sidecar } = setup({ last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });

    fireEvent.click(within(row("old-task")).getByRole("button", { name: /Open folder/ }));

    await waitFor(() => expect(sidecar.openedFolders).toEqual(["C:\\src\\shop\\.claude\\worktrees\\old-task"]));
  });

  it("refreshes with a new count of unreachable commits", async () => {
    const { sidecar } = setup({ last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });

    fireEvent.click(screen.getByRole("button", { name: "Refresh" }));

    await waitFor(() => expect(sent(sidecar, "repo.refresh")[0]?.params).toEqual({ repoId: "1", includeUnreachable: true }));
  });

  it("says plainly when a folder is not a repo, and can retry", async () => {
    const { sidecar } = setup({ last: "C:\\Users\\johan\\Downloads" });

    const alert = await screen.findByRole("alert");
    expect(within(alert).getByText("Could not open Downloads")).toBeTruthy();
    expect(within(alert).getByText("Not a git repository: C:\\Users\\johan\\Downloads")).toBeTruthy();
    fireEvent.click(within(alert).getByRole("button", { name: "Retry" }));
    await waitFor(() => expect(sent(sidecar, "repo.open")).toHaveLength(2));
    expect(within(await screen.findByRole("alert")).getByRole("button", { name: "Open another folder" })).toBeTruthy();
  });

  it("says why git cannot be used", async () => {
    const sidecar = new FakeSidecar();
    sidecar.info = { ...sidecar.info, gitVersion: "2.39.5", gitSupported: false, gitProblem: "Diffract needs git 2.40.0 or newer, but found 2.39.5." };
    setup({ sidecar, last: repoRoot });

    expect((await screen.findByRole("alert")).textContent).toContain("needs git 2.40.0");
    expect(sent(sidecar, "repo.open")).toEqual([]);
  });

  it("says when the sidecar does not answer", async () => {
    const sidecar = new FakeSidecar();
    sidecar.failures.set("app.info", { error: { code: -32603, message: "Something broke" } });
    setup({ sidecar });

    const alert = await screen.findByRole("alert");
    expect(within(alert).getByText("The sidecar did not answer")).toBeTruthy();
    expect(within(alert).getByText("Something broke")).toBeTruthy();
  });

  it("opens the repo again after the sidecar was restarted, and says so", async () => {
    const { sidecar, sidecarEvent } = setup({ last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });

    sidecarEvent({ kind: "restarted" });

    expect(screen.getByRole("status").textContent).toContain("stopped unexpectedly and was restarted");
    await waitFor(() => expect(sent(sidecar, "repo.open")).toHaveLength(2));
    await screen.findByRole("list", { name: "Leftovers" });
  });

  it("offers to try again when the sidecar keeps stopping", async () => {
    const { host, sidecar, sidecarEvent } = setup({ last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });

    sidecarEvent({ kind: "stopped" });
    const alert = screen.getByRole("alert");
    expect(within(alert).getByText("The sidecar keeps stopping")).toBeTruthy();
    fireEvent.click(within(alert).getByRole("button", { name: "Retry" }));

    await waitFor(() => expect(host.restartSidecar).toHaveBeenCalled());
    await waitFor(() => expect(sent(sidecar, "app.info").length).toBeGreaterThan(1));
  });
});
