import { forwardRef } from "react";
import type { CommitDetails, FileChange, FileChangeKind } from "../protocol";
import type { Marker } from "./markers";
import { MarkerChip } from "./MarkerChip";
import type { DetailsState } from "./useCommitDetails";

interface Props {
  state: DetailsState;
  markers: readonly Marker[];
  /** Selects a parent, when it is in the history read so far. */
  onSelect(commitId: string): void;
  isLoaded(commitId: string): boolean;
}

/** The selected commit: message, author and committer, parents, refs and changed files. */
export const CommitPanel = forwardRef<HTMLElement, Props>(function CommitPanel({ state, markers, onSelect, isLoaded }, ref) {
  return (
    <aside className="commit-panel" aria-label="Commit details" tabIndex={-1} ref={ref}>
      <PanelBody state={state} markers={markers} onSelect={onSelect} isLoaded={isLoaded} />
    </aside>
  );
});

function PanelBody({ state, ...props }: Props) {
  switch (state.kind) {
    case "none":
      return <p className="empty">Select a commit to see its details.</p>;
    case "loading":
      return <p className="empty">Loading…</p>;
    case "failed":
      return (
        <p className="empty" role="alert">
          Could not read this commit: {state.message}
        </p>
      );
    case "ready":
      return <Details details={state.details} {...props} />;
  }
}

function Details({ details: d, markers, onSelect, isLoaded }: Omit<Props, "state"> & { details: CommitDetails }) {
  return (
    <>
      <h2 className="commit-subject">{d.subject || "(no subject)"}</h2>
      {d.body && <pre className="commit-body">{d.body}</pre>}
      {markers.length > 0 && (
        <div className="markers">
          {markers.map((m) => (
            <MarkerChip key={`${m.kind}:${m.name}`} marker={m} />
          ))}
        </div>
      )}
      <dl className="facts">
        <dt>Commit</dt>
        <dd className="mono">{d.id}</dd>
        <dt>Author</dt>
        <dd>
          <Person name={d.authorName} email={d.authorEmail} at={d.authoredAt} />
        </dd>
        {(d.committerName !== d.authorName || d.committedAt !== d.authoredAt) && (
          <>
            <dt>Committer</dt>
            <dd>
              <Person name={d.committerName} email={d.committerEmail} at={d.committedAt} />
            </dd>
          </>
        )}
        <dt>{d.parentIds.length === 1 ? "Parent" : "Parents"}</dt>
        <dd>{d.parentIds.length === 0 ? "None, this is a root commit" : <Parents ids={d.parentIds} onSelect={onSelect} isLoaded={isLoaded} />}</dd>
      </dl>
      <h3 className="files-title">{filesTitle(d.files.length)}</h3>
      <ul className="files" aria-label="Changed files">
        {d.files.map((f) => (
          <FileRow key={`${f.oldPath ?? ""}>${f.path}`} file={f} />
        ))}
      </ul>
    </>
  );
}

function Person({ name, email, at }: { name: string; email: string; at: string }) {
  return (
    <>
      <span title={email}>{name}</span>, <time dateTime={at}>{new Date(at).toLocaleString()}</time>
    </>
  );
}

function Parents({ ids, onSelect, isLoaded }: { ids: string[] } & Pick<Props, "onSelect" | "isLoaded">) {
  return (
    <span className="parents">
      {ids.map((id) => (
        <button key={id} className="link mono" disabled={!isLoaded(id)} onClick={() => onSelect(id)} title={isLoaded(id) ? `Go to ${id}` : "Not loaded yet"}>
          {id.slice(0, 8)}
        </button>
      ))}
    </span>
  );
}

const fileLetters: Record<FileChangeKind, string> = { Added: "A", Deleted: "D", Modified: "M", Renamed: "R", Copied: "C", TypeChanged: "T", Unknown: "?" };
const fileClasses: Partial<Record<FileChangeKind, string>> = { Added: "add", Deleted: "del" };

function FileRow({ file: f }: { file: FileChange }) {
  return (
    <li title={f.oldPath ? `${f.oldPath} → ${f.path}` : f.path}>
      <b className={fileClasses[f.kind] ?? "mod"} aria-label={f.kind}>
        {fileLetters[f.kind]}
      </b>
      <span className="file-path">{f.path}</span>
      <span className="file-stat">{f.isBinary ? "binary" : `+${f.additions ?? 0} −${f.deletions ?? 0}`}</span>
    </li>
  );
}

function filesTitle(count: number): string {
  return count === 1 ? "1 file changed" : `${count} files changed`;
}
