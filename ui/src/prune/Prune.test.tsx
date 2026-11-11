import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App, LastRepoKey } from "../App";
import { remoteOf } from "../changes/RemoteBar";
import type { AppHost } from "../host";
import type { WorktreeChanges } from "../protocol";
import { RpcClient } from "../rpc";
import { FakeSidecar, sampleState } from "../testing/fakeSidecar";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

async function openRepo(sidecar = new FakeSidecar()) {
  vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null);
  const stored = new Map<string, string>([[LastRepoKey, sampleState.repoRoot]]);
  const host: AppHost = {
    transport: sidecar.transport,
    pickFolder: vi.fn(async () => null),
    onSidecarEvent: () => () => {},
    restartSidecar: vi.fn(async () => {}),
    storage: { get: (key) => stored.get(key) ?? null, set: (key, value) => void stored.set(key, value) },
  };
  render(<App host={host} client={new RpcClient(sidecar.transport)} clock={() => new Date("2026-10-21T09:30:00+02:00")} autoCloseMs={20} />);
  await within(await screen.findByRole("navigation", { name: "Repository" })).findByRole("button", { name: "main" });
  return sidecar;
}

const sent = (sidecar: FakeSidecar, method: string) => sidecar.transport.sent.filter((m) => m.method === method).map((m) => m.params);
const topBar = () => screen.getByRole("group", { name: "Branch" });
const dialogTitle = /Remove 2 branches that origin no longer has\?/;

function lose(sidecar: FakeSidecar, ...branches: string[]) {
  sidecar.cleanups.gone.set("origin", branches);
}

describe("Prune remote branches", () => {
  it("asks the remote from the top bar, lists what it lost with no backup, and removes it after the dialog", async () => {
    const sidecar = new FakeSidecar();
    lose(sidecar, "fix-1", "räksmörgås");
    await openRepo(sidecar);

    fireEvent.click(await within(topBar()).findByRole("button", { name: "Prune" }));
    const dialog = await screen.findByRole("dialog", { name: dialogTitle });

    expect(sent(sidecar, "cleanup.planPrune")).toEqual([{ repoId: sampleState.repoId, remote: "origin" }]);
    expect(within(dialog).getByText("Delete remote-tracking branch origin/fix-1, here only")).toBeTruthy();
    expect(within(dialog).getByText("Delete remote-tracking branch origin/räksmörgås, here only")).toBeTruthy();
    expect(within(dialog).queryByText(/backup tag/i, { selector: "li *" })).toBeNull();
    expect(within(dialog).getByText(/Nothing on origin is touched, and no backup tags are made/)).toBeTruthy();
    expect(sent(sidecar, "cleanup.apply")).toEqual([]);

    fireEvent.click(within(dialog).getByRole("button", { name: "Remove" }));

    await waitFor(() => expect(sent(sidecar, "cleanup.apply")).toHaveLength(1));
    expect(await screen.findByRole("button", { name: "Undo prune" })).toBeTruthy();
  });

  it("undoes the prune from the command window", async () => {
    const sidecar = new FakeSidecar();
    lose(sidecar, "fix-1", "räksmörgås");
    await openRepo(sidecar);
    fireEvent.click(await within(topBar()).findByRole("button", { name: "Prune" }));
    fireEvent.click(within(await screen.findByRole("dialog", { name: dialogTitle })).getByRole("button", { name: "Remove" }));

    fireEvent.click(await screen.findByRole("button", { name: "Undo prune" }));

    await waitFor(() => expect(sent(sidecar, "cleanup.undo")).toHaveLength(1));
  });

  it("prunes the remote whose Prune button in the sidebar was pressed", async () => {
    const sidecar = new FakeSidecar();
    lose(sidecar, "fix-1", "räksmörgås");
    await openRepo(sidecar);
    fireEvent.click(within(screen.getByRole("navigation", { name: "Repository" })).getByRole("button", { name: /^Remotes(, \d+)?$/ }));

    fireEvent.click(within(screen.getByRole("navigation", { name: "Repository" })).getByRole("button", { name: "Prune remote branches on origin" }));

    expect(await screen.findByRole("dialog", { name: dialogTitle })).toBeTruthy();
    expect(sent(sidecar, "cleanup.planPrune")).toEqual([{ repoId: sampleState.repoId, remote: "origin" }]);
  });

  it("removes nothing when the dialog is cancelled", async () => {
    const sidecar = new FakeSidecar();
    lose(sidecar, "fix-1", "räksmörgås");
    await openRepo(sidecar);
    fireEvent.click(await within(topBar()).findByRole("button", { name: "Prune" }));

    fireEvent.click(within(await screen.findByRole("dialog", { name: dialogTitle })).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog", { name: dialogTitle })).toBeNull();
    expect(sent(sidecar, "cleanup.apply")).toEqual([]);
  });

  it("says there is nothing to prune, without a dialog, when the remote has every branch", async () => {
    const sidecar = await openRepo();

    fireEvent.click(await within(topBar()).findByRole("button", { name: "Prune" }));

    expect(await screen.findByText("Nothing to prune: origin still has every branch you have a copy of.")).toBeTruthy();
    expect(screen.queryByRole("dialog", { name: /no longer has/ })).toBeNull();
    expect(sent(sidecar, "cleanup.apply")).toEqual([]);
  });

  it("removes nothing and says why when the remote cannot be reached", async () => {
    const sidecar = new FakeSidecar();
    sidecar.cleanups.unreachable.set("origin", "fatal: unable to access 'https://example.com/shop.git/'");
    await openRepo(sidecar);

    fireEvent.click(await within(topBar()).findByRole("button", { name: "Prune" }));

    expect(await screen.findByText(/Git could not ask origin which branches it has, so nothing was removed/)).toBeTruthy();
    expect(screen.queryByRole("dialog", { name: /no longer has/ })).toBeNull();
    expect(sent(sidecar, "cleanup.apply")).toEqual([]);
  });
});

describe("remoteOf", () => {
  const status = (upstream: string | null) => ({ upstream }) as WorktreeChanges;

  it("is the remote the branch follows", () => expect(remoteOf(status("upstream/feature/x"))).toBe("upstream"));
  it("is origin for a branch that follows nothing", () => expect(remoteOf(status(null))).toBe("origin"));
  it("is origin for an upstream that is not on a remote", () => expect(remoteOf(status("main"))).toBe("origin"));
});
