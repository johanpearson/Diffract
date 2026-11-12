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

const root = sampleCommits[1]!;
const branch = "fix/räksmörgås";
const remoteBranch = `origin/${branch}`;
const label = `Delete ${branch} on origin…`;
const title = `Delete ${branch} on origin?`;
const understand = "I understand this removes it on origin";

function setup(sidecar: FakeSidecar) {
  render(
    <HistoryHarness
      client={new RpcClient(sidecar.transport)}
      state={sampleState}
      now={new Date("2026-10-21T09:30:00+02:00")}
      openedPath={sampleState.repoRoot}
      storage={{ get: () => null, set: () => {} }}
    />,
  );
}

/** The root commit with `origin/fix/räksmörgås` on it. */
function withRemoteBranch(sidecar = new FakeSidecar()) {
  sidecar.refs = [...sidecar.refs, { name: remoteBranch, kind: "RemoteBranch", targetId: root.id, agent: null }];
  return sidecar;
}

const list = () => screen.getByRole("listbox", { name: "Commits" });
const sent = (sidecar: FakeSidecar, method: string) => sidecar.transport.sent.filter((m) => m.method === method).map((m) => m.params);
const hasRef = (sidecar: FakeSidecar) => sidecar.refs.some((r) => r.name === remoteBranch);

/** Opens the root commit's menu and picks `name`. */
async function pick(name: string) {
  await waitFor(() => expect(within(list()).getAllByRole("option")).toHaveLength(2));
  fireEvent.contextMenu(within(list()).getAllByRole("option")[1]!, { clientX: 40, clientY: 80 });
  fireEvent.click(within(screen.getByRole("menu")).getByRole("menuitem", { name }));
}

async function confirmDialog() {
  const dialog = await screen.findByRole("dialog", { name: title });
  fireEvent.click(within(dialog).getByRole("checkbox", { name: understand }));
  fireEvent.click(within(dialog).getByRole("button", { name: "Delete on origin" }));
}

describe("deleting a branch on its remote from the history menu", () => {
  it("asks the remote, then says it changes the remote for everyone, and sends nothing until the tick and the button", async () => {
    const sidecar = withRemoteBranch();
    setup(sidecar);
    await pick(label);

    const dialog = await screen.findByRole("dialog", { name: title });

    expect(sent(sidecar, "cleanup.planRemoteBranch")).toEqual([{ repoId: sampleState.repoId, remote: "origin", branch }]);
    expect(dialog.textContent).toContain("This changes origin, not only this computer.");
    expect(dialog.textContent).toContain("for everyone who uses origin");
    expect(within(dialog).getByText(`Delete branch ${branch} on origin, for everyone`)).toBeTruthy();
    const button = within(dialog).getByRole("button", { name: "Delete on origin" });
    expect(button).toHaveProperty("disabled", true);
    fireEvent.click(button);
    expect(sent(sidecar, "cleanup.apply")).toEqual([]);

    fireEvent.click(within(dialog).getByRole("checkbox", { name: understand }));
    expect(button).toHaveProperty("disabled", false);
    fireEvent.click(button);

    await waitFor(() => expect(sent(sidecar, "cleanup.apply")).toHaveLength(1));
    expect(hasRef(sidecar)).toBe(false);
  });

  it("undoes the delete from the command window", async () => {
    const sidecar = withRemoteBranch();
    setup(sidecar);
    await pick(label);
    await confirmDialog();

    fireEvent.click(await screen.findByRole("button", { name: "Undo delete" }));

    await waitFor(() => expect(sent(sidecar, "cleanup.undo")).toHaveLength(1));
    await waitFor(() => expect(hasRef(sidecar)).toBe(true));
  });

  it("lists the backup tag first when the branch has work nowhere else", async () => {
    const sidecar = withRemoteBranch();
    sidecar.cleanups.atRisk.set(remoteBranch, 3);
    setup(sidecar);
    await pick(label);

    const dialog = await screen.findByRole("dialog", { name: title });

    const steps = within(within(dialog).getByRole("list", { name: "Planned steps" })).getAllByText(/backup tag|Delete branch/).map((e) => e.textContent);
    expect(steps).toEqual([`Keep a backup tag archive/remote/${remoteBranch}-2026-10-21 for 3 commits that exist nowhere else`, `Delete branch ${branch} on origin, for everyone`]);
    expect(dialog.textContent).toContain("a backup tag keeps them here first");
  });

  it("removes nothing when the dialog is cancelled", async () => {
    const sidecar = withRemoteBranch();
    setup(sidecar);
    await pick(label);

    fireEvent.click(within(await screen.findByRole("dialog", { name: title })).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog", { name: title })).toBeNull();
    expect(sent(sidecar, "cleanup.apply")).toEqual([]);
    expect(hasRef(sidecar)).toBe(true);
  });

  it("opens no dialog for the remote's default branch, and says why", async () => {
    const sidecar = new FakeSidecar();
    setup(sidecar);
    await pick("Delete main on origin…");

    expect(await screen.findByText("main is the default branch of origin. Diffract does not delete it.")).toBeTruthy();
    expect(screen.queryByRole("dialog", { name: /^Delete main/ })).toBeNull();
    expect(sent(sidecar, "cleanup.apply")).toEqual([]);
  });

  it("opens no dialog when the remote cannot be reached, and says what git said", async () => {
    const sidecar = withRemoteBranch();
    sidecar.cleanups.unreachable.set("origin", "fatal: unable to access 'https://example.com/shop.git/'");
    setup(sidecar);
    await pick(label);

    expect((await screen.findByRole("alert")).textContent).toContain("fatal: unable to access");
    expect(screen.queryByRole("dialog", { name: title })).toBeNull();
    expect(sent(sidecar, "cleanup.apply")).toEqual([]);
    expect(hasRef(sidecar)).toBe(true);
  });

  it("shows the remote's own words when it refuses the delete, and keeps the branch", async () => {
    const sidecar = withRemoteBranch();
    sidecar.cleanups.rejectedByRemote.set(remoteBranch, `! [remote rejected] ${branch} (protected branch hook declined)`);
    setup(sidecar);
    await pick(label);

    await confirmDialog();

    await waitFor(() => expect(sent(sidecar, "cleanup.apply")).toHaveLength(1));
    expect(await screen.findByText(/protected branch hook declined/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Undo delete" })).toBeNull();
    expect(hasRef(sidecar)).toBe(true);
  });
});
