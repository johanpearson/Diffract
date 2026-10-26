import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { useEffect } from "react";
import type { CommandOutput } from "../protocol";
import type { RpcClient } from "../rpc";
import { CommandsProvider, useCommands, type CommandSpec } from "./Commands";
import type { CommandKind } from "./commandRun";

afterEach(cleanup);

/** A client that only delivers command.output, which the test sends itself. */
function outputClient() {
  const listeners = new Set<(output: CommandOutput) => void>();
  const client = { onNotification: (_method: string, listener: (output: CommandOutput) => void) => (listeners.add(listener), () => listeners.delete(listener)) };
  const send = (output: CommandOutput) => act(() => listeners.forEach((l) => l(output)));
  return { client: client as unknown as RpcClient, send };
}

/** A command whose answer the test gives, through `finish`. */
function deferred<T>() {
  let finish!: (value: T) => void;
  let fail!: (error: unknown) => void;
  const promise = new Promise<T>((resolve, reject) => ((finish = resolve), (fail = reject)));
  return { promise, finish: (value: T) => act(async () => finish(value)), fail: (error: unknown) => act(async () => fail(error)) };
}

function Runner({ spec, onResult }: { spec: CommandSpec<string>; onResult(result: string | null): void }) {
  const commands = useCommands();
  // Once, when it first shows.
  useEffect(() => void commands.run(spec).then(onResult), []);
  return <button>Behind the window</button>;
}

function start(spec: Partial<CommandSpec<string>> & Pick<CommandSpec<string>, "call">, autoCloseMs = 30) {
  const { client, send } = outputClient();
  const onResult = vi.fn();
  const full: CommandSpec<string> = { kind: "fetch", title: "Fetch from origin", outcome: (r) => ({ message: r, tone: "done" }), ...spec };
  render(
    <CommandsProvider client={client} autoCloseMs={autoCloseMs}>
      <Runner spec={full} onResult={onResult} />
    </CommandsProvider>,
  );
  return { send, onResult };
}

const commandWindow = () => screen.getByRole("dialog");
const state = () => within(commandWindow()).getByRole("status").textContent;
const lines = () => [...within(commandWindow()).getByRole("log", { name: "Git output" }).children].map((l) => l.textContent);

describe("the command window", () => {
  it("shows the command and git's output as it comes, then closes by itself after a success", async () => {
    const answer = deferred<string>();
    let operation = "";
    const { send, onResult } = start({ cancellable: true, call: (op) => ((operation = op), answer.promise) });

    expect(within(commandWindow()).getByRole("heading").textContent).toBe("Fetch from origin");
    expect(state()).toBe("Running");
    send({ operation, kind: "Command", text: "git fetch --progress origin" });
    send({ operation, kind: "Progress", text: "Receiving objects:  50% (1/2)" });
    expect(lines()).toEqual(["> git fetch --progress origin", "Receiving objects:  50% (1/2)"]);
    send({ operation, kind: "Line", text: "Receiving objects: 100% (2/2), done." });
    send({ operation: "op-other", kind: "Line", text: "someone else's" });
    expect(lines()).toEqual(["> git fetch --progress origin", "Receiving objects: 100% (2/2), done."]);

    await answer.finish("Fetched from origin.");

    expect(state()).toBe("Done");
    expect(commandWindow().textContent).toContain("Fetched from origin.");
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(onResult).toHaveBeenCalledWith("Fetched from origin.");
  });

  it("stays open after a warning, and Escape or Close closes it once it has ended", async () => {
    const answer = deferred<string>();
    start({ call: () => answer.promise, outcome: (r) => ({ message: r, tone: "warning" }) });

    fireEvent.keyDown(commandWindow(), { key: "Escape" });
    expect(screen.getByRole("dialog")).toBeTruthy();
    expect(within(commandWindow()).queryByRole("button", { name: "Close" })).toBeNull();
    await answer.finish("main and origin/main have both moved on.");

    expect(within(commandWindow()).getByRole("alert").textContent).toBe("main and origin/main have both moved on.");
    await new Promise((r) => setTimeout(r, 60));
    expect(state()).toBe("Nothing was changed");
    fireEvent.keyDown(commandWindow(), { key: "Escape" });
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("stays open after a failure, with what went wrong, and answers the caller with null", async () => {
    const answer = deferred<string>();
    const { onResult } = start({ call: () => answer.promise });

    await answer.fail(new Error("fatal: unable to access 'https://example.com/shop.git/'"));

    expect(state()).toBe("Failed");
    expect(within(commandWindow()).getByRole("alert").textContent).toContain("fatal: unable to access");
    await waitFor(() => expect(onResult).toHaveBeenCalledWith(null));
    fireEvent.click(within(commandWindow()).getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("offers a follow-up, and then waits for the user", async () => {
    const answer = deferred<string>();
    const undo = vi.fn();
    start({ kind: "reset", call: () => answer.promise, outcome: (r) => ({ message: r, tone: "done", followUp: { label: "Undo reset", run: undo } }) });

    await answer.finish("Reset main (soft).");
    await new Promise((r) => setTimeout(r, 60));
    fireEvent.click(within(commandWindow()).getByRole("button", { name: "Undo reset" }));

    expect(undo).toHaveBeenCalled();
    expect(screen.getByRole("dialog")).toBeTruthy();
  });

  it("offers Cancel only for what can be stopped", () => {
    start({ kind: "commit", title: "Commit", call: () => new Promise(() => {}) });

    expect(within(commandWindow()).queryByRole("button", { name: "Cancel" })).toBeNull();
  });

  it.each<CommandKind>(["fetch", "pull", "push", "commit", "switch", "branch", "tag", "reset"])("animates %s its own way while it runs, and stops after", async (kind) => {
    const answer = deferred<string>();
    start({ kind, call: () => answer.promise });
    const picture = () => commandWindow().querySelector("svg.command-animation")!;

    expect(picture().getAttribute("class")).toBe(`command-animation anim-${kind} is-running`);
    await answer.finish("Done.");
    expect(picture().getAttribute("class")).toBe(`command-animation anim-${kind}`);
    expect(picture().querySelector(".anim-badge.done")).not.toBeNull();
  });
});
