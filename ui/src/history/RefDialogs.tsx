import { useEffect, useId, useRef, useState, type FormEvent, type KeyboardEvent, type ReactNode } from "react";
import type { CommitEntry, RefNameKind, ResetMode } from "../protocol";
import type { RpcClient } from "../rpc";
import type { ResetPreview } from "./resetPreview";

interface ShellProps {
  title: string;
  busy: boolean;
  /** Why the action failed, from git or the sidecar. */
  error: string | null;
  submitLabel: string;
  canSubmit: boolean;
  onSubmit(): void;
  onCancel(): void;
  children: ReactNode;
}

/** A modal form: Escape or Cancel closes it, Enter submits it when it can. */
function DialogShell({ title, busy, error, submitLabel, canSubmit, onSubmit, onCancel, children }: ShellProps) {
  const titleId = useId();
  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === "Escape") onCancel();
  };
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (canSubmit && !busy) onSubmit();
  };
  return (
    <div className="overlay">
      <form className="dialog ref-dialog" role="dialog" aria-modal="true" aria-labelledby={titleId} onKeyDown={onKeyDown} onSubmit={submit}>
        <h1 id={titleId}>{title}</h1>
        {children}
        {error && (
          <p className="dialog-error" role="alert">
            {error}
          </p>
        )}
        <div className="panel-actions">
          <button type="button" className="abtn" onClick={onCancel}>
            Cancel
          </button>
          <button type="submit" className="abtn primary" disabled={!canSubmit || busy}>
            {submitLabel}
          </button>
        </div>
      </form>
    </div>
  );
}

interface NameProps {
  kind: RefNameKind;
  commit: CommitEntry;
  client: RpcClient;
  repoId: string;
  worktree: string;
  busy: boolean;
  error: string | null;
  onCreate(name: string, extra: { switchTo: boolean; message: string }): void;
  onCancel(): void;
}

/** Asks for a new branch or tag's name, and says before anything runs if git would not take it. */
export function NameDialog({ kind, commit, client, repoId, worktree, busy, error, onCreate, onCancel }: NameProps) {
  const [name, setName] = useState("");
  const [switchTo, setSwitchTo] = useState(true);
  const [message, setMessage] = useState("");
  const check = useNameCheck(client, { repoId, worktree, kind, name });
  const noun = kind === "Branch" ? "branch" : "tag";
  return (
    <DialogShell
      title={`Create ${noun} at ${commit.id.slice(0, 8)}`}
      busy={busy}
      error={error}
      submitLabel={`Create ${noun}`}
      canSubmit={name.trim() !== "" && !check.checking && check.problem === null}
      onSubmit={() => onCreate(name, { switchTo, message })}
      onCancel={onCancel}
    >
      <p className="muted">{commit.subject || "(no subject)"}</p>
      <label className="field">
        <span>Name</span>
        <input autoFocus value={name} onChange={(event) => setName(event.target.value)} aria-describedby={check.problem ? "ref-name-problem" : undefined} spellCheck={false} />
      </label>
      <p id="ref-name-problem" className="field-problem" aria-live="polite">
        {name.trim() === "" ? "" : (check.problem ?? "")}
      </p>
      {kind === "Branch" ? (
        <label className="check-field">
          <input type="checkbox" checked={switchTo} onChange={(event) => setSwitchTo(event.target.checked)} />
          Switch to it
        </label>
      ) : (
        <label className="field">
          <span>Message (optional)</span>
          <textarea rows={3} value={message} onChange={(event) => setMessage(event.target.value)} />
        </label>
      )}
    </DialogShell>
  );
}

/** Asks the sidecar about the name a short while after the user stops typing. Only the newest answer counts. */
function useNameCheck(client: RpcClient, params: { repoId: string; worktree: string; kind: RefNameKind; name: string }) {
  const [answer, setAnswer] = useState<{ name: string; problem: string | null } | null>(null);
  const { repoId, worktree, kind, name } = params;
  const latest = useRef(name);
  latest.current = name;
  useEffect(() => {
    if (name.trim() === "") return;
    const timer = setTimeout(() => {
      client
        .call("ref.checkName", { repoId, worktree, kind, name })
        .then(({ problem }) => latest.current === name && setAnswer({ name, problem }))
        .catch(() => latest.current === name && setAnswer({ name, problem: null }));
    }, 200);
    return () => clearTimeout(timer);
  }, [client, repoId, worktree, kind, name]);
  const current = answer?.name === name ? answer : null;
  return { checking: current === null, problem: current?.problem ?? null };
}

interface ResetProps {
  branch: string;
  commit: CommitEntry;
  preview: ResetPreview;
  busy: boolean;
  error: string | null;
  onReset(mode: ResetMode): void;
  onCancel(): void;
}

const modes: { mode: ResetMode; label: string; detail: string }[] = [
  { mode: "Soft", label: "Soft", detail: "Their changes stay staged, ready to commit again." },
  { mode: "Mixed", label: "Mixed", detail: "Their changes stay in your files, unstaged." },
];

/** How many leaving commits are listed by name. The rest are counted. */
const listed = 8;

/** Says which commits leave the branch and asks soft or mixed. Files are never touched either way. */
export function ResetDialog({ branch, commit, preview, busy, error, onReset, onCancel }: ResetProps) {
  const [mode, setMode] = useState<ResetMode>("Soft");
  return (
    <DialogShell title={`Reset ${branch} to ${commit.id.slice(0, 8)}?`} busy={busy} error={error} submitLabel="Reset" canSubmit onSubmit={() => onReset(mode)} onCancel={onCancel}>
      <LeavingCommits branch={branch} preview={preview} />
      {preview.pushedTo && (
        <p className="warn-text" role="note">
          {preview.pushedTo} already has some of these commits. After the reset, pushing {branch} needs a force push, which Diffract does not do yet.
        </p>
      )}
      <fieldset className="reset-modes">
        <legend>Keep the changes</legend>
        {modes.map((m) => (
          <label key={m.mode} className="check-field">
            <input type="radio" name="reset-mode" checked={mode === m.mode} onChange={() => setMode(m.mode)} />
            <span>
              <strong>{m.label}:</strong> {m.detail}
            </span>
          </label>
        ))}
      </fieldset>
    </DialogShell>
  );
}

function LeavingCommits({ branch, preview: { leaving, arriving } }: { branch: string; preview: ResetPreview }) {
  const joining = arriving > 0 ? ` ${count(arriving)} ${arriving === 1 ? "comes" : "come"} onto it.` : "";
  if (leaving.length === 0) return <p>No commits leave {branch}.{joining}</p>;
  return (
    <>
      <p>
        {count(leaving.length)} {leaving.length === 1 ? "leaves" : "leave"} {branch}:{joining}
      </p>
      <ul className="leaving" aria-label="Commits leaving the branch">
        {leaving.slice(0, listed).map((c) => (
          <li key={c.id}>
            <code>{c.id.slice(0, 8)}</code> {c.subject || "(no subject)"}
          </li>
        ))}
        {leaving.length > listed && <li className="muted">and {leaving.length - listed} more</li>}
      </ul>
    </>
  );
}

function count(n: number): string {
  return n === 1 ? "1 commit" : `${n} commits`;
}
