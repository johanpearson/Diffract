import { useEffect, useRef, useState } from "react";
import type { WorktreeChanges } from "../protocol";

interface Props {
  status: WorktreeChanges;
  stagedCount: number;
  /** Nothing can be committed while an action runs, or until an in-use worktree's warning is accepted. */
  locked: boolean;
  /** Resolves with whether it committed. */
  onCommit(message: string, amend: boolean): Promise<boolean>;
}

/**
 * The message, and Commit. Commit is enabled when something is staged and the message has a subject; Amend also
 * when nothing is staged, to reword. Ctrl+Enter commits, wherever the focus is in the Changes view.
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
    </div>
  );
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
