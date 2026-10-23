import type { KeyboardEvent, ReactNode } from "react";
import { changeName, letterOf, type Entry, type Lists } from "./entries";

interface Props {
  lists: Lists;
  selected: string | null;
  onSelect(entry: Entry): void;
  /** Stages an unstaged row, or unstages a staged one. */
  onToggle(entry: Entry): void;
  onStageAll(): void;
  /** Nothing can be staged while an action runs, or until an in-use worktree's warning is accepted. */
  locked: boolean;
}

/**
 * The Unstaged and Staged lists, as one list box so up and down move through both. Space stages or unstages the
 * selected row, as its button does.
 */
export function FileLists({ lists, selected, onSelect, onToggle, onStageAll, locked }: Props) {
  const all = [...lists.unstaged, ...lists.staged];
  const stageable = lists.unstaged.filter((e) => e.blocked === null);

  const onKeyDown = (event: KeyboardEvent) => {
    const index = all.findIndex((e) => e.key === selected);
    const next = moveTo(event.key, index, all.length);
    if (next !== null) {
      event.preventDefault();
      onSelect(all[next]!);
    } else if (event.key === " " && index >= 0) {
      event.preventDefault();
      if (!locked && all[index]!.blocked === null) onToggle(all[index]!);
    }
  };

  return (
    <div className="change-lists" role="listbox" aria-label="Changed files" tabIndex={0} onKeyDown={onKeyDown} aria-activedescendant={selected ? idOf(selected) : undefined}>
      <Group title="Unstaged" entries={lists.unstaged} empty="Nothing to stage. The worktree matches the index." {...{ selected, onSelect, onToggle, locked }}>
        <button className="abtn small" tabIndex={-1} disabled={locked || stageable.length === 0} onClick={onStageAll}>
          Stage all
        </button>
      </Group>
      <Group title="Staged" entries={lists.staged} empty="Nothing staged yet. Stage files to commit them." {...{ selected, onSelect, onToggle, locked }} />
    </div>
  );
}

function moveTo(key: string, index: number, count: number): number | null {
  if (count === 0) return null;
  if (key === "ArrowDown") return Math.min(index + 1, count - 1);
  if (key === "ArrowUp") return Math.max(index - 1, 0);
  if (key === "Home") return 0;
  return key === "End" ? count - 1 : null;
}

interface GroupProps extends Pick<Props, "selected" | "onSelect" | "onToggle" | "locked"> {
  title: string;
  entries: Entry[];
  empty: string;
  children?: ReactNode;
}

function Group({ title, entries, empty, children, ...rows }: GroupProps) {
  const headingId = `changes-${title.toLowerCase()}`;
  return (
    <div role="group" aria-labelledby={headingId} className="change-group">
      <h2 className="change-heading">
        <span id={headingId}>
          {title} <span className="change-count">{entries.length}</span>
        </span>
        {children}
      </h2>
      {entries.length === 0 ? <p className="empty small">{empty}</p> : entries.map((entry) => <Row key={entry.key} entry={entry} {...rows} />)}
    </div>
  );
}

function Row({ entry, selected, onSelect, onToggle, locked }: { entry: Entry } & Pick<Props, "selected" | "onSelect" | "onToggle" | "locked">) {
  const { file } = entry;
  const action = entry.side === "unstaged" ? "Stage" : "Unstage";
  return (
    <div id={idOf(entry.key)} role="option" aria-selected={entry.key === selected} className="change-row" onClick={() => onSelect(entry)} title={file.path}>
      <span className={`change-letter ${letterClass(letterOf(entry))}`} title={changeName(entry)} aria-label={changeName(entry)}>
        {letterOf(entry)}
      </span>
      <span className="change-path">{file.oldPath && entry.side === "staged" ? `${file.oldPath} → ${file.path}` : file.path}</span>
      {entry.blocked === null ? (
        <button
          className="abtn small"
          tabIndex={-1}
          disabled={locked}
          aria-label={`${action} ${file.path}`}
          onClick={(event) => {
            event.stopPropagation();
            onToggle(entry);
          }}
        >
          {action}
        </button>
      ) : (
        <span className="change-blocked" title={entry.blocked}>
          {file.kind === "Conflicted" ? "Conflict" : "Not stageable"}
        </span>
      )}
    </div>
  );
}

/** An id for the row, unique for every path: each character outside letters and digits becomes its code. */
function idOf(key: string): string {
  return `change-${key.replace(/[^A-Za-z0-9]/g, (c) => `_${c.codePointAt(0)!.toString(16)}`)}`;
}

function letterClass(letter: string): string {
  if (letter === "A") return "add";
  if (letter === "D" || letter === "U") return "del";
  return "mod";
}
