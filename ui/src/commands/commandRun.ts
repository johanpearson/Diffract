import type { CommandOutput, GitOutputKind } from "../protocol";

/** What a command does, which picks its animation. */
export type CommandKind = "fetch" | "pull" | "push" | "commit" | "switch" | "branch" | "tag" | "reset";

/** A line in the window. A progress line is replaced by the next line git writes. */
export interface OutputLine {
  kind: GitOutputKind;
  text: string;
}

/** Something the user can do next, from the window, such as undoing a reset. */
export interface FollowUp {
  label: string;
  run(): void;
}

/**
 * How it ended. Done closes the window by itself, unless there is a follow-up. A warning means git did nothing and
 * the user has something to do about it, so it stays open, as a failure does.
 */
export interface Outcome {
  message: string;
  tone: "done" | "warning";
  followUp?: FollowUp;
}

export type RunState = "running" | "done" | "warning" | "failed";

/** One command in the window: what it is, what git has said so far, and how it ended. */
export interface CommandRun {
  id: string;
  kind: CommandKind;
  title: string;
  lines: OutputLine[];
  state: RunState;
  /** How it ended, in plain words. Null while it runs. */
  message: string | null;
  followUp: FollowUp | null;
  cancellable: boolean;
}

export function started(id: string, kind: CommandKind, title: string, cancellable: boolean): CommandRun {
  return { id, kind, title, lines: [], state: "running", message: null, followUp: null, cancellable };
}

/** Adds a line for this run. A line after a progress line takes its place. Lines for another run are ignored. */
export function withOutput(run: CommandRun, output: CommandOutput): CommandRun {
  if (output.operation !== run.id) return run;
  const lines = run.lines.at(-1)?.kind === "Progress" && output.kind !== "Command" ? run.lines.slice(0, -1) : run.lines;
  return { ...run, lines: [...lines, { kind: output.kind, text: output.text }] };
}

export function finished(run: CommandRun, outcome: Outcome): CommandRun {
  return { ...run, state: outcome.tone, message: outcome.message, followUp: outcome.followUp ?? null };
}

export function failed(run: CommandRun, message: string): CommandRun {
  return { ...run, state: "failed", message };
}

/** Only a plain success closes by itself: anything else has something to read or do. */
export function closesByItself(run: CommandRun): boolean {
  return run.state === "done" && run.followUp === null;
}
