import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App, LastRepoKey } from "../App";
import type { AppHost } from "../host";
import { FlashMs } from "../model/rows";
import { RpcClient } from "../rpc";
import { FakeSidecar, sampleState } from "../testing/fakeSidecar";

afterEach(cleanup);

/** The app on Cleanup with the sample repo open, against a fake sidecar. */
async function setup() {
  const sidecar = new FakeSidecar();
  const stored = new Map([[LastRepoKey, sampleState.repoRoot]]);
  const host: AppHost = {
    transport: sidecar.transport,
    pickFolder: vi.fn(async () => null),
    onSidecarEvent: () => () => {},
    restartSidecar: vi.fn(async () => {}),
    storage: { get: (key) => stored.get(key) ?? null, set: (key, value) => void stored.set(key, value) },
  };
  render(<App host={host} client={new RpcClient(sidecar.transport)} clock={() => new Date("2026-10-21T09:30:00+02:00")} initialView="leftovers" autoCloseMs={0} />);
  await screen.findByRole("list", { name: "Leftovers" });
  return sidecar;
}

const rowNames = () => within(screen.getByRole("list", { name: "Leftovers" })).getAllByRole("listitem").map((li) => li.getAttribute("aria-label"));
/** The number in the summary box for a verdict, by its class. */
const count = (verdictClass: string) => document.querySelector(`.summary .sum.${verdictClass} b`)?.textContent;

describe("hiding rows", () => {
  it("leaves the row out of the list and its counts, and lists it under Hidden", async () => {
    const sidecar = await setup();

    fireEvent.click(screen.getByRole("button", { name: "Hide copilot/fix-login" }));

    await waitFor(() => expect(rowNames()).not.toContain("copilot/fix-login"), { timeout: FlashMs * 3 });
    expect(count("review")).toBe("0");
    fireEvent.click(screen.getByText("Hidden (1)"));
    expect(within(screen.getByRole("list", { name: "Hidden rows" })).getByRole("listitem").getAttribute("aria-label")).toBe("copilot/fix-login");
    expect(sidecar.cleanups.asked.find((a) => a.method === "cleanup.hide")?.params).toEqual({
      repoId: "1",
      leftover: { kind: "LocalBranch", name: "copilot/fix-login", path: null },
    });
  });

  it("shows a hidden row again", async () => {
    await setup();
    fireEvent.click(screen.getByRole("button", { name: "Hide copilot/fix-login" }));
    fireEvent.click(await screen.findByText("Hidden (1)"));

    fireEvent.click(screen.getByRole("button", { name: "Show copilot/fix-login again" }));

    await waitFor(() => expect(rowNames()).toContain("copilot/fix-login"));
    expect(screen.queryByText(/^Hidden \(/)).toBeNull();
  });

  it("never cleans up a row that was ticked and then hidden", async () => {
    await setup();
    fireEvent.click(screen.getByRole("checkbox", { name: "Select copilot/fix-login" }));

    fireEvent.click(screen.getByRole("button", { name: "Hide copilot/fix-login" }));

    await waitFor(() => expect((screen.getByRole("button", { name: "Clean up selected" }) as HTMLButtonElement).disabled).toBe(true), { timeout: FlashMs * 3 });
  });
});
