import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type Dispatch, type ReactNode, type SetStateAction } from "react";
import { messageOf } from "../model/format";
import { isCancelled, type RpcClient } from "../rpc";
import { closesByItself, failed, finished, started, withOutput, type CommandKind, type CommandRun, type Outcome } from "./commandRun";
import { CommandWindow } from "./CommandWindow";

/** A git command to run in the window. */
export interface CommandSpec<T> {
  kind: CommandKind;
  /** The window's title, such as "Pull from origin/main". */
  title: string;
  /** Runs it. `operation` goes to the sidecar, so git's output comes back for this window. */
  call(operation: string, signal: AbortSignal): Promise<T>;
  outcome(result: T): Outcome;
  /** Fetch, pull and push can be stopped. Changes to the repo itself run to the end. */
  cancellable?: boolean;
}

export interface Commands {
  /** Null when it failed or was cancelled: the window says why, so the caller has nothing to show. */
  run<T>(spec: CommandSpec<T>): Promise<T | null>;
  /** A command is running. */
  busy: boolean;
}

const CommandsContext = createContext<Commands | null>(null);

/** The commands of the app, which every view runs git through. */
export function useCommands(): Commands {
  const commands = useContext(CommandsContext);
  if (!commands) throw new Error("useCommands needs a CommandsProvider");
  return commands;
}

/** About how long a plain success stays on screen before the window closes by itself. */
export const AutoCloseMs = 1500;

/**
 * Runs one git command at a time and shows it in the command window: the command, git's output as it comes, and how
 * it ended. A plain success closes the window by itself; a warning or a failure stays until closed.
 */
export function CommandsProvider({ client, autoCloseMs = AutoCloseMs, children }: { client: RpcClient; autoCloseMs?: number; children: ReactNode }) {
  const [current, setCurrent] = useState<CommandRun | null>(null);
  const runner = useRunner(setCurrent);
  useEffect(() => client.onNotification("command.output", (output) => setCurrent((run) => run && withOutput(run, output))), [client]);
  useAutoClose(current, autoCloseMs, () => setCurrent(null));

  const busy = current?.state === "running";
  const commands = useMemo<Commands>(() => ({ run: runner.run, busy }), [runner.run, busy]);
  return (
    <CommandsContext.Provider value={commands}>
      {children}
      {current && <CommandWindow run={current} onCancel={runner.cancel} onClose={() => setCurrent(null)} />}
    </CommandsContext.Provider>
  );
}

/** Runs a command and keeps the window's run up to date. A later command replaces an earlier one's window. */
function useRunner(setCurrent: Dispatch<SetStateAction<CommandRun | null>>) {
  const controller = useRef<AbortController | null>(null);
  const ids = useRef(0);

  const run = useCallback(
    async <T,>(spec: CommandSpec<T>): Promise<T | null> => {
      const id = `op-${++ids.current}`;
      const mine = new AbortController();
      controller.current = mine;
      const update = (change: (run: CommandRun) => CommandRun | null) => setCurrent((r) => (r?.id === id ? change(r) : r));
      setCurrent(started(id, spec.kind, spec.title, spec.cancellable ?? false));
      try {
        const result = await spec.call(id, mine.signal);
        update((r) => finished(r, spec.outcome(result)));
        return result;
      } catch (error) {
        update((r) => ended(r, error));
        return null;
      }
    },
    [setCurrent],
  );

  return { run, cancel: () => controller.current?.abort() };
}

/** Cancelling leaves nothing to read, so the window closes. */
function ended(run: CommandRun, error: unknown): CommandRun | null {
  return isCancelled(error) ? null : failed(run, messageOf(error));
}

function useAutoClose(run: CommandRun | null, ms: number, close: () => void) {
  const latest = useRef(close);
  latest.current = close;
  const closes = run !== null && closesByItself(run);
  const id = run?.id;
  useEffect(() => {
    if (!closes) return;
    const timer = setTimeout(() => latest.current(), ms);
    return () => clearTimeout(timer);
  }, [closes, ms, id]);
}
