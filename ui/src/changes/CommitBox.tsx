import { useEffect, useRef, useState } from "react";
import type { WorktreeChanges } from "../protocol";

interface Props {
  status: WorktreeChanges;
  stagedCount: number;
  /** Nothing can be committed while an action runs, or until an in-use worktree's warning is accepted. */
  locked: boolean;
  /** Resolves with whether it committed. */
  onCommit(message: string, amend: boolean): Promise<boolean>;
  /** Stashes with the message in the box. Resolves with whether it stashed. */
  onStash(message: string, includeUntracked: boolean): Promise<boolean>;
}

/**
 * The message, and Commit. Commit is enabled when something is staged and the message has a subject; Amend also
 * when nothing is staged, to reword. Ctrl+Enter commits, wherever the focus is in the Changes view. Below it,
 * "Stash changes" puts the changes away under the same message.
 */
export function CommitBox(props: Props) {
  const form = useCommitForm(props);
  useCtrlEnter(form.commit);

  return (
    <div className="commit-box">
      <textarea
        aria-label="Commit message"
        placeholder="Commit message: a subject, then a blank line and more if you like"
        value={form.message}
        onChange={(event) => form.setMessage(event.target.value)}
      />
      <AmendToggle checked={form.amend} blocked={form.amendBlocked} onChange={form.setAmend} />
      <button className="abtn primary" disabled={!form.canCommit} onClick={() => void form.commit()} title="Commit (Ctrl+Enter)">
        {buttonText(props.stagedCount, form.amend)}
      </button>
      <StashRow status={props.status} locked={props.locked} onStash={(includeUntracked) => props.onStash(form.message, includeUntracked)} />
    </div>
  );
}

/**
 * "Stash changes", and "Include new files" only when there are new files. The message stays in the box, as the
 * stash keeps the work it describes.
 */
function StashRow({ status, locked, onStash }: { status: WorktreeChanges; locked: boolean; onStash(includeUntracked: boolean): Promise<boolean> }) {
  const [includeUntracked, setIncludeUntracked] = useState(false);
  const hasNew = status.files.some((f) => f.kind === "Untracked");
  const blocked = stashBlockedBy(status, hasNew && includeUntracked);
  return (
    <div className="commit-stash">
      <button className="abtn" disabled={locked || blocked !== null} title={blocked ?? "Put the changes away in a stash, with the message above as its name"} onClick={() => void onStash(hasNew && includeUntracked)}>
        Stash changes
      </button>
      {hasNew && (
        <label className="toggle" title="Stash new files too, not only changes to files git already has">
          <input type="checkbox" checked={includeUntracked} onChange={(event) => setIncludeUntracked(event.target.checked)} />
          Include new files
        </label>
      )}
    </div>
  );
}

/** Why there is nothing to stash, or null. Git refuses to stash while a file has a conflict. */
function stashBlockedBy(status: WorktreeChanges, withNewFiles: boolean): string | null {
  if (status.files.some((f) => f.kind === "Conflicted")) return "A file has a conflict, and git does not stash until it is resolved.";
  const stashable = status.files.some((f) => f.kind === "Tracked" || (withNewFiles && f.kind === "Untracked"));
  return stashable ? null : "There are no changes to stash. New files are stashed only with \"Include new files\".";
}

function useCommitForm({ status, stagedCount, locked, onCommit }: Props) {
  const [message, setMessage] = useState("");
  const [amend, setAmend] = useState(false);
  const amendBlocked = amendBlockedBy(status);
  const canCommit = !locked && hasSubject(message) && (stagedCount > 0 || (amend && amendBlocked === null));

  const commit = async () => {
    if (!canCommit) return;
    // When it did not commit, the command window says why, and the message stays for another try.
    if (!(await onCommit(message, amend))) return;
    setMessage("");
    setAmend(false);
  };

  return { message, setMessage, amend, setAmend, amendBlocked, canCommit, commit };
}

/** Ctrl+Enter runs the latest `commit`, which sees the current message, wherever the focus is. */
function useCtrlEnter(commit: () => Promise<void>) {
  const latest = useRef(commit);
  latest.current = commit;
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (!event.ctrlKey || event.key !== "Enter") return;
      event.preventDefault();
      void latest.current();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, []);
}

function AmendToggle({ checked, blocked, onChange }: { checked: boolean; blocked: string | null; onChange(value: boolean): void }) {
  return (
    <>
      <label className="toggle" title={blocked ?? "Replace the last commit with this message and what is staged"}>
        <input type="checkbox" checked={checked} disabled={blocked !== null} onChange={(event) => onChange(event.target.checked)} />
        Amend last commit
      </label>
      {blocked && <p className="commit-note">{blocked}</p>}
    </>
  );
}

function hasSubject(message: string): boolean {
  return (message.split("\n")[0] ?? "").trim().length > 0;
}

/** Why amend is off: there is no commit yet, or the last one is already pushed. Null when it is allowed. */
function amendBlockedBy(status: WorktreeChanges): string | null {
  if (status.headCommit === null) return "There is no commit to amend yet.";
  return status.headOnRemote ? `The last commit is on ${status.headOnRemote}, so amending it would rewrite pushed work.` : null;
}

function buttonText(count: number, amend: boolean): string {
  if (amend) return "Amend last commit";
  if (count === 0) return "Commit";
  return count === 1 ? "Commit 1 file" : `Commit ${count} files`;
}
