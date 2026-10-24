import { useCallback, useEffect, useState } from "react";
import { messageOf } from "../model/format";
import type { CommitEntry, RefNameKind, ResetMode, ResetResult } from "../protocol";
import type { RpcClient } from "../rpc";
import type { SwitchTo } from "./commitMenu";

/** A dialog the menu opened, for one commit. */
export type RefDialog = { kind: RefNameKind | "Reset"; commit: CommitEntry };

/** What came of the last action. A reset carries itself, so the note can offer to undo it. */
export interface RefNote {
  text: string;
  tone: "ok" | "error";
  undo?: ResetResult;
}

export interface RefActions {
  dialog: RefDialog | null;
  /** An action is running. Dialogs keep their buttons disabled until it answers. */
  busy: boolean;
  note: RefNote | null;
  /** Why the open dialog's action failed. It stays open, so the user can change their mind or try again. */
  dialogError: string | null;
  open(dialog: RefDialog): void;
  close(): void;
  copy(text: string, what: string): void;
  switchTo(target: SwitchTo): void;
  createBranch(name: string, switchTo: boolean): void;
  createTag(name: string, message: string): void;
  reset(mode: ResetMode): void;
  undo(): void;
  dismiss(): void;
}

/** The history menu's actions in one worktree, the dialog that asks for more, and what came of the last one. */
export function useRefActions(client: RpcClient, repoId: string, worktree: string): RefActions {
  const [dialog, setDialog] = useState<RefDialog | null>(null);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<RefNote | null>(null);
  const [dialogError, setDialogError] = useState<string | null>(null);
  useEffect(() => setDialogError(null), [dialog]);

  /** Runs one action. It answers with the note to show, and closes the dialog it came from. */
  const run = useCallback(async (action: () => Promise<RefNote>, fromDialog: boolean) => {
    setBusy(true);
    try {
      setNote(await action());
      setDialog(null);
    } catch (error) {
      if (fromDialog) setDialogError(messageOf(error));
      else setNote({ text: messageOf(error), tone: "error" });
    } finally {
      setBusy(false);
    }
  }, []);

  const where = { repoId, worktree };
  const commit = dialog?.commit.id ?? "";
  return {
    dialog,
    busy,
    note,
    dialogError,
    open: (next) => {
      setNote(null);
      setDialog(next);
    },
    close: () => setDialog(null),
    copy: (text, what) => void run(async () => (await navigator.clipboard.writeText(text), { text: `Copied ${what}.`, tone: "ok" }), false),
    switchTo: (target) => void run(async () => ok((await client.call("branch.switch", { ...where, ...target })).message), false),
    createBranch: (name, switchTo) => void run(async () => ok((await client.call("branch.create", { ...where, commit, name, switch: switchTo })).message), true),
    createTag: (name, message) => void run(async () => ok((await client.call("tag.create", { ...where, commit, name, message: message || null })).message), true),
    reset: (mode) => void run(async () => resetNote(await client.call("branch.reset", { ...where, commit, mode })), true),
    undo: () => note?.undo && void run(async () => ok(undoneText(await client.call("branch.undoReset", { ...where, reset: note.undo! }))), false),
    dismiss: () => setNote(null),
  };
}

function ok(text: string): RefNote {
  return { text, tone: "ok" };
}

function resetNote(reset: ResetResult): RefNote {
  return { text: `Reset ${reset.branch} (${reset.mode.toLowerCase()}) from ${reset.oldCommit.slice(0, 8)} to ${reset.newCommit.slice(0, 8)}.`, tone: "ok", undo: reset };
}

function undoneText(undone: ResetResult): string {
  return `Undid the reset: ${undone.branch} is back at ${undone.newCommit.slice(0, 8)}.`;
}
