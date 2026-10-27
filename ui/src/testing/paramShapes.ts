import { refNameKinds, resetModes, terminalKinds, type Methods } from "../protocol";
import { resetResult, selectedLeftover, stepRef } from "./protocolShapes";
import { arrayOf, boolean, nullable, object, oneOf, optional, string, type Shape } from "./shapes";

/** The methods that take params. */
type WithParams = { [M in keyof Methods]: Methods[M]["params"] extends undefined ? never : M }[keyof Methods];

type Params<M extends WithParams> = Methods[M]["params"];

/** Every field of every choice of a "one of these" params type, so its shape has to name them all. */
type AllFields<U> = { [K in U extends unknown ? keyof U : never]: unknown };

const inWorktree = object<Params<"changes.status">>({ repoId: string, worktree: string });

/** Read by the sidecar for any request that changes the repo, so its params types do not name it. See `Watched`. */
const watched = { operation: optional(string) };

const remote = object<Params<"remote.fetch">>({ repoId: string, worktree: string, ...watched });

const stage = object<AllFields<Params<"changes.stage">>>({
  repoId: string,
  worktree: string,
  paths: optional(arrayOf(string)),
  hunk: optional(object<{ path: string; oldPath?: string | null; id: string }>({ path: string, oldPath: optional(nullable(string)), id: string })),
});

/**
 * The params the UI sends with each method. The sidecar's tests write a sample of each from its own params types,
 * and the contract test checks it against these, so a field the two sides name or type differently fails there.
 */
export const paramShapes: { [M in WithParams]: Shape } = {
  "repo.open": object<Params<"repo.open">>({ path: string }),
  "repo.refresh": object<Params<"repo.refresh">>({ repoId: string, includeUnreachable: boolean }),
  "repo.close": object<Params<"repo.close">>({ repoId: string }),
  "system.openFolder": object<Params<"system.openFolder">>({ path: string }),
  "system.openTerminal": object<Params<"system.openTerminal">>({ path: string, terminal: optional(oneOf(terminalKinds)) }),
  "git.update": object<Params<"git.update">>({ version: string }),
  "history.read": object<Params<"history.read">>({ repoId: string, token: string, checkpoints: boolean, stashes: boolean }),
  "history.update": object<Params<"history.update">>({ repoId: string, tips: arrayOf(string), checkpoints: boolean, stashes: boolean }),
  "history.refs": object<Params<"history.refs">>({ repoId: string }),
  "history.details": object<Params<"history.details">>({ repoId: string, commitId: string }),
  "history.file": object<Params<"history.file">>({ repoId: string, blobId: string }),
  "changes.status": inWorktree,
  "changes.file": object<Params<"changes.file">>({
    repoId: string,
    worktree: string,
    path: string,
    oldPath: optional(nullable(string)),
    staged: boolean,
    headId: nullable(string),
    indexId: nullable(string),
  }),
  "changes.stage": stage,
  "changes.unstage": stage,
  "changes.commit": object<Params<"changes.commit">>({ repoId: string, worktree: string, message: string, amend: boolean, ...watched }),
  "remote.fetch": remote,
  "remote.pull": remote,
  "remote.push": remote,
  "ref.checkName": object<Params<"ref.checkName">>({ repoId: string, worktree: string, kind: oneOf(refNameKinds), name: string }),
  "branch.create": object<Params<"branch.create">>({ repoId: string, worktree: string, commit: string, name: string, switch: boolean, ...watched }),
  "branch.switch": object<AllFields<Params<"branch.switch">>>({
    repoId: string,
    worktree: string,
    branch: optional(string),
    remoteBranch: optional(string),
    commit: optional(string),
    ...watched,
  }),
  "tag.create": object<Params<"tag.create">>({ repoId: string, worktree: string, commit: string, name: string, message: optional(nullable(string)), ...watched }),
  "branch.reset": object<Params<"branch.reset">>({ repoId: string, worktree: string, commit: string, mode: oneOf(resetModes), ...watched }),
  "branch.undoReset": object<Params<"branch.undoReset">>({ repoId: string, worktree: string, reset: resetResult, ...watched }),
  "cleanup.plan": object<Params<"cleanup.plan">>({ repoId: string, leftovers: arrayOf(selectedLeftover) }),
  "cleanup.apply": object<Params<"cleanup.apply">>({ repoId: string, planId: string, leaveOut: optional(nullable(arrayOf(stepRef))), ...watched }),
  "cleanup.history": object<Params<"cleanup.history">>({ repoId: string }),
  "cleanup.inspect": object<Params<"cleanup.inspect">>({ repoId: string, leftover: selectedLeftover }),
  "cleanup.hide": object<Params<"cleanup.hide">>({ repoId: string, leftover: selectedLeftover }),
  "cleanup.unhide": object<Params<"cleanup.unhide">>({ repoId: string, leftover: selectedLeftover }),
  "cleanup.undo": object<Params<"cleanup.undo">>({ repoId: string, cleanupId: string, ...watched }),
};
