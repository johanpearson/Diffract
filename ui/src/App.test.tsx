import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App, LastRepoKey, TerminalKey } from "./App";
import type { View } from "./components/Tabs";
import type { AppHost, SidecarEvent } from "./host";
import { RpcClient } from "./rpc";
import { GitUpdateCheckKey, OneDayMs } from "./model/useGitUpdate";
import { FakeSidecar, sampleState } from "./testing/fakeSidecar";

afterEach(cleanup);

const repoRoot = sampleState.repoRoot;

/** Renders the app against a fake sidecar. `last` is the repo remembered from the previous run. */
/** `terminal` is the one remembered from the previous run. */
/** `remembered` is anything else stored from earlier runs. */
function setup({
  last = null as string | null,
  sidecar = new FakeSidecar(),
  folder = repoRoot as string | null,
  terminal = null as string | null,
  remembered = {} as Record<string, string>,
  view = "leftovers" as View,
} = {}) {
  const stored = new Map<string, string>([...(last ? [[LastRepoKey, last] as const] : []), ...Object.entries(remembered)]);
  if (terminal) stored.set(TerminalKey, terminal);
  const handlers = new Set<(event: SidecarEvent) => void>();
  const host: AppHost = {
    transport: sidecar.transport,
    pickFolder: vi.fn(async () => folder),
    onSidecarEvent: (handler) => (handlers.add(handler), () => handlers.delete(handler)),
    restartSidecar: vi.fn(async () => {}),
    storage: { get: (key) => stored.get(key) ?? null, set: (key, value) => void stored.set(key, value) },
  };
  render(<App host={host} client={new RpcClient(sidecar.transport)} clock={() => new Date("2026-10-21T09:30:00+02:00")} initialView={view} />);
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

  it("switches to the history and back with the tabs, from the mouse or the arrow keys", async () => {
    vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null);
    const { sidecar } = setup({ last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });
    expect(sent(sidecar, "history.read")).toHaveLength(0);

    fireEvent.click(screen.getByRole("tab", { name: "History" }));

    expect(await screen.findByRole("listbox", { name: "Commits" })).toBeTruthy();
    expect(screen.getByRole("tabpanel", { name: "History" })).toBeTruthy();
    expect(sent(sidecar, "history.read")[0]?.params).toMatchObject({ repoId: sampleState.repoId });
    fireEvent.keyDown(screen.getByRole("tab", { name: "History" }), { key: "ArrowRight" });
    expect(document.activeElement).toBe(screen.getByRole("tab", { name: "Changes" }));
    expect(await screen.findByRole("listbox", { name: "Changed files" })).toBeTruthy();
    fireEvent.keyDown(screen.getByRole("tab", { name: "Changes" }), { key: "ArrowRight" });
    expect(await screen.findByRole("list", { name: "Leftovers" })).toBeTruthy();
    expect(document.activeElement).toBe(screen.getByRole("tab", { name: "Worktrees and agents" }));
    vi.restoreAllMocks();
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

  it("opens a terminal in the repo, in a worktree row, and from the sidebar's menu", async () => {
    const { sidecar } = setup({ last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });
    const worktree = sampleState.snapshot.worktrees[1]!.path;

    fireEvent.click(await screen.findByRole("button", { name: "Open in Windows Terminal" }));
    fireEvent.click(within(row("räksmörgås fix")).getByRole("button", { name: /Open terminal in/ }));
    fireEvent.contextMenu(screen.getByRole("listitem", { name: "claude/räksmörgås-fix" }), { clientX: 40, clientY: 80 });
    fireEvent.click(within(screen.getByRole("menu")).getByRole("menuitem", { name: "Open in Windows Terminal" }));

    await waitFor(() =>
      expect(sidecar.openedTerminals).toEqual([`${repoRoot} in WindowsTerminal`, `${worktree} in WindowsTerminal`, `${worktree} in WindowsTerminal`]),
    );
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("lists every installed terminal in the sidebar menu, the current one first", async () => {
    setup({ last: repoRoot, terminal: "GitBash" });
    await screen.findByRole("button", { name: "Open in Git Bash" });

    fireEvent.contextMenu(screen.getByRole("listitem", { name: "main" }), { clientX: 40, clientY: 80 });

    expect(within(screen.getByRole("menu")).getAllByRole("menuitem").map((item) => item.textContent)).toEqual([
      "Open in Git Bash",
      "Open in Windows Terminal",
      "Open in Windows PowerShell",
      "Open folder",
    ]);
  });

  it("opens the chosen terminal from the arrow menu and remembers it", async () => {
    const { sidecar, stored } = setup({ last: repoRoot });
    fireEvent.click(await screen.findByRole("button", { name: "Choose terminal" }));

    const chooser = screen.getByRole("menu", { name: "Choose terminal" });
    expect(within(chooser).getByRole("menuitemradio", { name: "Windows Terminal" }).getAttribute("aria-checked")).toBe("true");
    fireEvent.click(within(chooser).getByRole("menuitemradio", { name: "Git Bash" }));

    await waitFor(() => expect(sidecar.openedTerminals).toEqual([`${repoRoot} in GitBash`]));
    expect(stored.get(TerminalKey)).toBe("GitBash");
    expect(screen.getByRole("button", { name: "Open in Git Bash" })).toBeTruthy();
    expect(within(row("räksmörgås fix")).getByRole("button", { name: /Open terminal in/ }).textContent).toBe("Open in Git Bash");
  });

  it("a terminal chosen in the sidebar menu becomes the current one", async () => {
    const { stored } = setup({ last: repoRoot });
    await screen.findByRole("button", { name: "Open in Windows Terminal" });

    fireEvent.contextMenu(screen.getByRole("listitem", { name: "main" }), { clientX: 40, clientY: 80 });
    fireEvent.click(within(screen.getByRole("menu")).getByRole("menuitem", { name: "Open in Windows PowerShell" }));

    expect(stored.get(TerminalKey)).toBe("WindowsPowerShell");
    expect(screen.getByRole("button", { name: "Open in Windows PowerShell" })).toBeTruthy();
  });

  it("falls back to the default when the remembered terminal is no longer installed", async () => {
    const sidecar = new FakeSidecar();
    sidecar.terminals = [{ id: "WindowsPowerShell", name: "Windows PowerShell" }, { id: "CommandPrompt", name: "Command Prompt" }];
    setup({ sidecar, last: repoRoot, terminal: "GitBash" });

    expect(await screen.findByRole("button", { name: "Open in Windows PowerShell" })).toBeTruthy();
  });

  it("still opens the default terminal when the sidecar cannot list them", async () => {
    const sidecar = new FakeSidecar();
    sidecar.failures.set("system.listTerminals", { error: { code: -32603, message: "Something broke" } });
    setup({ sidecar, last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });

    fireEvent.click(screen.getByRole("button", { name: "Open terminal" }));

    expect(screen.queryByRole("button", { name: "Choose terminal" })).toBeNull();
    await waitFor(() => expect(sidecar.openedTerminals).toEqual([repoRoot]));
  });

  it("offers a terminal only on worktree rows, not on folders or branches", async () => {
    setup({ last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });

    expect(within(row("old-task")).queryByRole("button", { name: /Open terminal/ })).toBeNull();
    expect(within(row("copilot/fix-login")).queryByRole("button", { name: /Open terminal/ })).toBeNull();
  });

  it("works the sidebar menu from the keyboard and gives focus back when it closes", async () => {
    setup({ last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });
    const entry = screen.getByRole("listitem", { name: "main" });
    entry.focus();

    fireEvent.contextMenu(entry);
    const menu = screen.getByRole("menu", { name: "Actions for main" });
    expect(document.activeElement?.textContent).toBe("Open in Windows Terminal");
    fireEvent.keyDown(menu, { key: "ArrowUp" });
    expect(document.activeElement?.textContent).toBe("Open folder");
    fireEvent.keyDown(menu, { key: "ArrowDown" });
    expect(document.activeElement?.textContent).toBe("Open in Windows Terminal");
    fireEvent.keyDown(menu, { key: "ArrowDown" });
    expect(document.activeElement?.textContent).toBe("Open in Windows PowerShell");
    fireEvent.keyDown(menu, { key: "Escape" });

    expect(screen.queryByRole("menu")).toBeNull();
    expect(document.activeElement).toBe(entry);
  });

  it("closes the sidebar menu on a click elsewhere", async () => {
    setup({ last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });

    fireEvent.contextMenu(screen.getByRole("listitem", { name: "main" }), { clientX: 40, clientY: 80 });
    fireEvent.mouseDown(document.body);

    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("disables the menu's actions for a worktree whose folder is gone", async () => {
    const sidecar = new FakeSidecar();
    const gone = { ...sampleState.snapshot.worktrees[1]!, isPrunable: true, prunableReason: "gitdir file points to non-existent location" };
    sidecar.repos.set(repoRoot.toLowerCase(), { ...sampleState, snapshot: { ...sampleState.snapshot, worktrees: [sampleState.snapshot.worktrees[0]!, gone] } });
    setup({ sidecar, last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });

    fireEvent.contextMenu(screen.getByRole("listitem", { name: "claude/räksmörgås-fix" }), { clientX: 40, clientY: 80 });

    for (const item of within(screen.getByRole("menu")).getAllByRole("menuitem")) expect((item as HTMLButtonElement).disabled).toBe(true);
  });

  it("says so when a terminal cannot be opened", async () => {
    const { sidecar } = setup({ last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });
    sidecar.failures.set("system.openTerminal", { error: { code: -32602, message: `The folder does not exist: ${repoRoot}` } });

    fireEvent.click(await screen.findByRole("button", { name: "Open in Windows Terminal" }));

    expect((await screen.findByRole("status")).textContent).toContain("Could not open a terminal: The folder does not exist");
  });

  describe("opening on History while the scan runs", () => {
    function scanningSidecar() {
      const sidecar = new FakeSidecar();
      sidecar.scanOnOpen = true;
      return sidecar;
    }
    const nav = () => screen.getByRole("navigation", { name: "Repository" });

    it("opens on History, first among the tabs", async () => {
      vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null);
      setup({ last: repoRoot, view: "history", sidecar: scanningSidecar() });

      expect(await screen.findByRole("listbox", { name: "Commits" })).toBeTruthy();
      expect(screen.getAllByRole("tab").map((t) => [t.textContent, t.getAttribute("aria-selected")])).toEqual([
        ["History", "true"],
        ["Changes", "false"],
        ["Worktrees and agents", "false"],
      ]);
      vi.restoreAllMocks();
    });

    it("says it is still looking for leftovers, and blocks Refresh until the scan is done", async () => {
      setup({ last: repoRoot, sidecar: scanningSidecar() });

      expect(await screen.findByText(/Looking for leftovers/)).toBeTruthy();
      expect((screen.getByRole("button", { name: "Refresh" }) as HTMLButtonElement).disabled).toBe(true);
      expect(within(nav()).getByLabelText("still looking").textContent).toBe("…");
      expect(screen.getByText(/Unreachable commits not counted yet/)).toBeTruthy();
    });

    it("shows the leftovers when the scan finishes, without highlighting them as news", async () => {
      const sidecar = scanningSidecar();
      setup({ last: repoRoot, sidecar });
      await screen.findByText(/Looking for leftovers/);

      act(() => sidecar.finishScan("1"));

      expect(await screen.findByRole("list", { name: "Leftovers" })).toBeTruthy();
      expect(rowNames()).toEqual(["old-task", "copilot/fix-login", "räksmörgås fix"]);
      expect(within(leftoverList()).getAllByRole("listitem").map((li) => li.getAttribute("data-flash"))).toEqual([null, null, null]);
      expect((screen.getByRole("button", { name: "Refresh" }) as HTMLButtonElement).disabled).toBe(false);
      expect(within(nav()).getByLabelText("2 not in use")).toBeTruthy();
    });

    it("says why when the scan fails, and lets Refresh try again", async () => {
      const sidecar = scanningSidecar();
      setup({ last: repoRoot, sidecar });
      await screen.findByText(/Looking for leftovers/);

      act(() => sidecar.failScan("1", "git fsck failed: bad object in pack."));

      expect((await screen.findByRole("alert")).textContent).toContain("git fsck failed: bad object in pack. Refresh to try again.");
      fireEvent.click(screen.getByRole("button", { name: "Refresh" }));
      expect(await screen.findByRole("list", { name: "Leftovers" })).toBeTruthy();
    });

    it("uses the scan's result when it arrives before the answer to repo.open", async () => {
      const sidecar = scanningSidecar();
      sidecar.held.add("repo.open");
      setup({ last: repoRoot, sidecar });
      await waitFor(() => expect(sent(sidecar, "repo.open")).toHaveLength(1));

      act(() => sidecar.finishScan("1"));
      act(() => sidecar.release());

      expect(await screen.findByRole("list", { name: "Leftovers" })).toBeTruthy();
      expect(screen.queryByText(/Looking for leftovers/)).toBeNull();
    });
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

describe("Git for Windows update", () => {
  const newer = { status: "UpdateAvailable", installedVersion: "2.47.1", availableVersion: "2.55.0.5", problem: null, downloadPage: "https://gitforwindows.org/" } as const;
  const now = new Date("2026-10-21T09:30:00+02:00").getTime();
  const codeScene = { program: "CodeScene cs-ide tool for windows", startedBy: "Visual Studio Code", processes: 2 };

  function withUpdate(sidecar = new FakeSidecar()) {
    sidecar.gitUpdate = { ...newer };
    return sidecar;
  }

  const remembered = (check: object, checkedAt = now - 60_000) => ({ [GitUpdateCheckKey]: JSON.stringify({ checkedAt, check }) });
  const dialog = () => screen.getByRole("dialog");
  const offer = () => screen.queryByText(/is available/);

  async function confirmUpdate() {
    fireEvent.click(await screen.findByRole("button", { name: "Update…" }));
    await within(dialog()).findByText("No other program is using git right now.");
    fireEvent.click(within(dialog()).getByRole("button", { name: "Update" }));
  }

  // --- when winget is asked -----------------------------------------------------

  it("asks winget only once the last repo is open, so opening it is not slowed down", async () => {
    const sidecar = withUpdate();
    sidecar.held.add("repo.open");
    setup({ last: repoRoot, sidecar });

    await waitFor(() => expect(sent(sidecar, "repo.open")).toHaveLength(1));
    expect(sent(sidecar, "git.checkUpdate")).toHaveLength(0);
    act(() => sidecar.release());

    await screen.findByRole("list", { name: "Leftovers" });
    await waitFor(() => expect(sent(sidecar, "git.checkUpdate")).toHaveLength(1));
    expect(await screen.findByText(/Git for Windows 2\.55\.0\.5 is available/)).toBeTruthy();
  });

  it("asks on a first start with no repo, says nothing when git is current, and remembers the answer", async () => {
    const { sidecar, stored } = setup();
    await screen.findByRole("heading", { name: "Open a repo" });

    await waitFor(() => expect(sent(sidecar, "git.checkUpdate")).toHaveLength(1));
    expect(offer()).toBeNull();
    expect(JSON.parse(stored.get(GitUpdateCheckKey)!)).toEqual({ checkedAt: now, check: sidecar.gitUpdate });
  });

  it("uses an answer from the last day instead of asking winget again", async () => {
    const { sidecar } = setup({ last: repoRoot, remembered: remembered(newer) });

    expect(await screen.findByText(/Git for Windows 2\.55\.0\.5 is available/)).toBeTruthy();
    expect(sent(sidecar, "git.checkUpdate")).toHaveLength(0);
  });

  it.each([
    ["more than a day old", remembered(newer, now - OneDayMs - 1)],
    ["about another git", remembered({ ...newer, installedVersion: "2.43.0" })],
    ["unreadable", { [GitUpdateCheckKey]: "{not json" }],
  ])("asks winget again when the remembered answer is %s", async (_, stored) => {
    const { sidecar } = setup({ remembered: stored });

    await waitFor(() => expect(sent(sidecar, "git.checkUpdate")).toHaveLength(1));
  });

  it("does not remember an answer that was unknown", async () => {
    const sidecar = new FakeSidecar();
    sidecar.gitUpdate = { status: "Unknown", installedVersion: "2.47.1", availableVersion: null, problem: "winget did not answer within 60 seconds.", downloadPage: "https://gitforwindows.org/" };
    const { stored } = setup({ sidecar });

    await waitFor(() => expect(sent(sidecar, "git.checkUpdate")).toHaveLength(1));
    expect(stored.has(GitUpdateCheckKey)).toBe(false);
  });

  it("keeps the screen clean: no button to check, and nothing said when git is current", async () => {
    const { sidecar } = setup({ last: repoRoot });
    await screen.findByRole("list", { name: "Leftovers" });

    await waitFor(() => expect(sent(sidecar, "git.checkUpdate")).toHaveLength(1));
    expect(screen.queryByRole("button", { name: /check for/i })).toBeNull();
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("'Not now' hides the offer until a newer version comes along", async () => {
    const { sidecar, stored, sidecarEvent } = setup({ sidecar: withUpdate() });
    fireEvent.click(await screen.findByRole("button", { name: "Not now" }));
    expect(offer()).toBeNull();

    // With no repo open, a restarted sidecar makes the app start over. The remembered answer is the same version.
    sidecarEvent({ kind: "restarted" });
    await screen.findByRole("heading", { name: "Open a repo" });
    expect(offer()).toBeNull();

    sidecar.gitUpdate = { ...newer, availableVersion: "2.56.0.1" };
    stored.delete(GitUpdateCheckKey);
    sidecarEvent({ kind: "restarted" });

    expect(await screen.findByText(/Git for Windows 2\.56\.0\.1 is available/)).toBeTruthy();
  });

  // --- confirming -----------------------------------------------------------------

  it("asks first, names both versions and the license, and installs nothing until confirmed", async () => {
    const { sidecar } = setup({ sidecar: withUpdate() });
    fireEvent.click(await screen.findByRole("button", { name: "Update…" }));

    expect(within(dialog()).getByRole("heading", { name: "Update Git for Windows?" })).toBeTruthy();
    expect(dialog().textContent).toContain("From 2.47.1 to 2.55.0.5.");
    expect(dialog().textContent).toContain("accepts the Git for Windows license (GPL-2.0)");
    expect(document.activeElement).toBe(within(dialog()).getByRole("button", { name: "Cancel" }));
    expect(sent(sidecar, "git.update")).toHaveLength(0);
  });

  it("names the programs using git before it starts, and can look again", async () => {
    const sidecar = withUpdate();
    sidecar.gitInUse = [codeScene, { program: "Git Bash", startedBy: null, processes: 3 }];
    setup({ sidecar });
    fireEvent.click(await screen.findByRole("button", { name: "Update…" }));

    const users = await within(dialog()).findByRole("list", { name: "Programs using git" });
    expect(within(users).getAllByRole("listitem").map((li) => li.textContent)).toEqual(["CodeScene cs-ide tool for windows in Visual Studio Code", "Git Bash"]);
    expect(within(dialog()).getByRole("alert").textContent).toContain("The update will stop unless you close these programs first");

    sidecar.gitInUse = [];
    fireEvent.click(within(dialog()).getByRole("button", { name: "Check again" }));

    expect(await within(dialog()).findByText("No other program is using git right now.")).toBeTruthy();
    expect(sent(sidecar, "git.inUse")).toHaveLength(2);
  });

  it("cancelling, or Escape, closes the dialog and installs nothing", async () => {
    const { sidecar } = setup({ sidecar: withUpdate() });
    fireEvent.click(await screen.findByRole("button", { name: "Update…" }));

    fireEvent.click(within(dialog()).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("dialog")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Update…" }));
    fireEvent.keyDown(dialog(), { key: "Escape" });
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(sent(sidecar, "git.update")).toHaveLength(0);
  });

  // --- running ----------------------------------------------------------------------

  it("updates quietly in the background, keeps git calls away until it is done, then shows the new version", async () => {
    const sidecar = withUpdate();
    sidecar.held.add("git.update");
    setup({ last: repoRoot, sidecar });
    await screen.findByRole("list", { name: "Leftovers" });

    await confirmUpdate();

    // The installer shows its own window, so Diffract adds nothing on screen meanwhile.
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.queryByRole("status")).toBeNull();
    expect(offer()).toBeNull();
    expect(sent(sidecar, "git.update")[0]?.params).toEqual({ version: "2.55.0.5" });
    expect(screen.getByRole("button", { name: "Refresh" })).toHaveProperty("disabled", true);
    expect(screen.getAllByRole("button", { name: "Open repo…" })[0]).toHaveProperty("disabled", true);

    sidecar.gitUpdate = { ...newer, status: "UpToDate", installedVersion: "2.55.0", availableVersion: null };
    sidecar.info = { ...sidecar.info, gitVersion: "2.55.0" };
    act(() => sidecar.release());

    expect(await screen.findByText("git 2.55.0 · sidecar 0.1.0")).toBeTruthy();
    await waitFor(() => expect(sent(sidecar, "repo.refresh").at(-1)?.params).toEqual({ repoId: "1", includeUnreachable: false }));
    await waitFor(() => expect(sent(sidecar, "git.checkUpdate")).toHaveLength(2));
    expect(screen.getByRole("button", { name: "Refresh" })).toHaveProperty("disabled", false);
    expect(screen.queryByRole("status")).toBeNull();
    expect(offer()).toBeNull();
  });

  it("says when Windows must restart to finish", async () => {
    const sidecar = withUpdate();
    sidecar.gitUpdateResult = { outcome: "RestartNeeded", message: "Git for Windows 2.55.0.5 is installed. Restart Windows to finish the update.", inUse: [] };
    setup({ sidecar });

    await confirmUpdate();

    expect(await screen.findByText(/Restart Windows to finish the update/)).toBeTruthy();
  });

  it("names the programs that stopped the installer, and offers to try again or download", async () => {
    const sidecar = withUpdate();
    sidecar.gitUpdateResult = { outcome: "Failed", message: "Git for Windows was not updated, because these programs are using git. Close them, then try again.", inUse: [codeScene] };
    setup({ sidecar });

    await confirmUpdate();

    expect(await screen.findByText(/was not updated, because these programs are using git/)).toBeTruthy();
    expect(screen.getByRole("list", { name: "Programs using git" }).textContent).toBe("CodeScene cs-ide tool for windows in Visual Studio Code");
    fireEvent.click(screen.getByRole("button", { name: "Open download page" }));
    await waitFor(() => expect(sidecar.downloadPageOpened).toBe(1));

    fireEvent.click(screen.getByRole("button", { name: "Try again…" }));
    expect(within(dialog()).getByRole("heading", { name: "Update Git for Windows?" })).toBeTruthy();
  });

  // --- git that is too old ------------------------------------------------------------

  const tooOld = { gitVersion: "2.39.5", gitSupported: false, gitProblem: "Diffract needs git 2.40.0 or newer, but found 2.39.5." };

  it("offers the update where it says git is too old, even with a fresh answer remembered, and starts over once it works", async () => {
    const sidecar = withUpdate();
    sidecar.info = { ...sidecar.info, ...tooOld };
    sidecar.gitUpdate = { ...newer, installedVersion: "2.39.5" };
    setup({ last: repoRoot, sidecar, remembered: remembered({ ...newer, status: "UpToDate", installedVersion: "2.39.5", availableVersion: null }) });

    const alert = await screen.findByRole("alert");
    fireEvent.click(await within(alert).findByRole("button", { name: "Update to 2.55.0.5…" }));
    expect(dialog().textContent).toContain("From 2.39.5 to 2.55.0.5.");
    expect(offer()).toBeNull();

    sidecar.info = { ...sidecar.info, gitVersion: "2.55.0", gitSupported: true, gitProblem: null };
    await within(dialog()).findByText("No other program is using git right now.");
    fireEvent.click(within(dialog()).getByRole("button", { name: "Update" }));

    expect(await screen.findByRole("list", { name: "Leftovers" })).toBeTruthy();
    expect(sent(sidecar, "repo.open")).toHaveLength(1);
  });

  it("offers the download page, and says why, when winget cannot update git that is too old", async () => {
    const sidecar = new FakeSidecar();
    sidecar.info = { ...sidecar.info, ...tooOld };
    sidecar.gitUpdate = { status: "Unknown", installedVersion: "2.39.5", availableVersion: null, problem: "winget is not installed, so Diffract cannot check for a newer Git for Windows.", downloadPage: "https://gitforwindows.org/" };
    setup({ sidecar });

    const alert = await screen.findByRole("alert");
    expect(await within(alert).findByText(/winget is not installed/)).toBeTruthy();
    fireEvent.click(within(alert).getByRole("button", { name: "Open download page" }));

    await waitFor(() => expect(sidecar.downloadPageOpened).toBe(1));
  });
});
