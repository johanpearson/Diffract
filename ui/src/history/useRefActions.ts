import { useEffect, useRef, useState } from "react";
import { useCommands, type CommandSpec } from "../commands/Commands";
import type { Outcome } from "../commands/commandRun";
import { messageOf } from "../model/format";
import type { CommitEntry, RefChange, RefNameKind, ResetMode, ResetResult } from "../protocol";
import type { RpcClient } from "../rpc";
import type { SwitchTo } from "./commitMenu";

/** A dialog the menu opened, for one commit. */
export type RefDialog = { kind: RefNameKind | "Reset"; commit: CommitEntry };

/** What came of copying, the one action that runs no git. */
export interface RefNote {
  text: string;
  tone: "ok" | "error";
}

export interface RefActions {
  dialog: RefDialog | null;
  /** A command is running. Dialogs keep their buttons disabled until it ends. */
  busy: boolean;
  note: RefNote | null;
  open(dialog: RefDialog): void;
  close(): void;
  copy(text: string, what: string): void;
  switchTo(target: SwitchTo): void;
  createBranch(name: string, switchTo: boolean): void;
  createTag(name: string, message: string): void;
  reset(mode: ResetMode): void;
  dismiss(): void;
}

/** How long a note that needs nothing from the user stays. */
export const NoteMs = 2500;

/**
 * The history menu's actions in one worktree, and the dialog that asks for more. Each runs in the command window,
 * which says what came of it; a reset's window offers to undo it.
 */
export function useRefActions(client: RpcClient, repoId: string, worktree: string): RefActions {
  const commands = useCommands();
  const [dialog, setDialog] = useState<RefDialog | null>(null);
  const [note, setNote] = useState<RefNote | null>(null);
  useFadingNote(note, () => setNote(null));

  /** The window takes over from the dialog it came from. */
  const run = <T,>(spec: CommandSpec<T>) => {
    setDialog(null);
    setNote(null);
    void commands.run(spec);
  };
  const where = { repoId, worktree };
  const commit = dialog?.commit.id ?? "";
  const undo = (reset: ResetResult) =>
    run({
      kind: "reset",
      title: `Undo the reset of ${reset.branch}`,
      call: (operation) => client.call("branch.undoReset", { ...where, reset, operation }),
      outcome: (undone) => ({ message: `Undid the reset: ${undone.branch} is back at ${short(undone.newCommit)}.`, tone: "done" }),
    });

  return {
    dialog,
    busy: commands.busy,
    note,
    open: (next) => {
      setNote(null);
      setDialog(next);
    },
    close: () => setDialog(null),
    copy: (text, what) =>
      void navigator.clipboard.writeText(text).then(
        () => setNote({ text: `Copied ${what}.`, tone: "ok" }),
        (error: unknown) => setNote({ text: messageOf(error), tone: "error" }),
      ),
    switchTo: (target) =>
      run({ kind: "switch", title: switchTitle(target), call: (operation) => client.call("branch.switch", { ...where, ...target, operation }), outcome: changed }),
    createBranch: (name, switchTo) =>
      run({ kind: "branch", title: `Create branch ${name}`, call: (operation) => client.call("branch.create", { ...where, commit, name, switch: switchTo, operation }), outcome: changed }),
    createTag: (name, message) =>
      run({ kind: "tag", title: `Create tag ${name}`, call: (operation) => client.call("tag.create", { ...where, commit, name, message: message || null, operation }), outcome: changed }),
    reset: (mode) =>
      run({
        kind: "reset",
        title: `Reset to ${short(commit)} (${mode.toLowerCase()})`,
        call: (operation) => client.call("branch.reset", { ...where, commit, mode, operation }),
        outcome: (reset) => ({ message: resetText(reset), tone: "done", followUp: { label: "Undo reset", run: () => undo(reset) } }),
      }),
    dismiss: () => setNote(null),
  };
}

/** A note that only says something went well goes away by itself. */
function useFadingNote(note: RefNote | null, clear: () => void) {
  const latest = useRef(clear);
  latest.current = clear;
  const fades = note?.tone === "ok";
  useEffect(() => {
    if (!fades) return;
    const timer = setTimeout(() => latest.current(), NoteMs);
    return () => clearTimeout(timer);
  }, [fades, note]);
}

function changed(change: RefChange): Outcome {
  return { message: change.message, tone: "done" };
}

function switchTitle(target: SwitchTo): string {
  if ("branch" in target) return `Switch to ${target.branch}`;
  if ("remoteBranch" in target) return `Switch to ${target.remoteBranch}`;
  return `Check out ${short(target.commit)}`;
}

function resetText(reset: ResetResult): string {
  return `Reset ${reset.branch} (${reset.mode.toLowerCase()}) from ${short(reset.oldCommit)} to ${short(reset.newCommit)}.`;
}

function short(commit: string): string {
  return commit.slice(0, 8);
}
