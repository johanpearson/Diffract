import { useState, type MouseEvent } from "react";
import type { RepoState, TerminalInfo, TerminalKind, WorktreeInfo } from "../protocol";
import { folderName } from "../model/format";
import { ContextMenu, type MenuItem } from "./ContextMenu";

interface Props {
  state: RepoState;
  /** The installed terminals. Empty when the sidecar could not list them; then the default opens. */
  terminals: TerminalInfo[];
  /** The one a plain "Open" uses: the last one chosen, or the default. */
  terminal: TerminalInfo | null;
  onOpenFolder(path: string): void;
  /** Without `kind`, the current terminal. With it, that one, which becomes the current one. */
  onOpenTerminal(path: string, kind?: TerminalKind): void;
}

/** The repo's worktrees with their state, and the Leftovers entry with the count of everything not in use. */
export function Sidebar({ state, terminals, terminal, onOpenFolder, onOpenTerminal }: Props) {
  const [menu, setMenu] = useState<{ worktree: WorktreeInfo; x: number; y: number } | null>(null);
  const [chooser, setChooser] = useState<{ x: number; y: number } | null>(null);
  const inUse = new Set(state.leftovers.filter((l) => l.verdict === "InUse" && l.path).map((l) => l.path!.toLowerCase()));
  const notInUse = state.summary.total - state.summary.inUse;

  return (
    <nav className="sidebar" aria-label="Repository">
      <div className="repo">
        <div className="repo-name">{folderName(state.repoRoot)}</div>
        <div className="repo-path" title={state.repoRoot}>
          {state.repoRoot}
        </div>
        <div className="split">
          <button className="abtn small" onClick={() => onOpenTerminal(state.repoRoot)}>
            {terminal ? `Open in ${terminal.name}` : "Open terminal"}
          </button>
          {terminals.length > 1 && (
            <button
              className="abtn small"
              aria-label="Choose terminal"
              aria-haspopup="menu"
              aria-expanded={chooser !== null}
              onClick={(event) => {
                const rect = event.currentTarget.getBoundingClientRect();
                setChooser({ x: rect.left, y: rect.bottom + 2 });
              }}
            >
              ▾
            </button>
          )}
        </div>
      </div>
      <h2 id="worktrees-title">Worktrees</h2>
      <ul className="worktrees" aria-labelledby="worktrees-title">
        {state.snapshot.worktrees.map((w) => (
          <WorktreeEntry key={w.path} worktree={w} inUse={inUse.has(w.path.toLowerCase())} onMenu={(event) => setMenu({ worktree: w, ...menuPosition(event) })} />
        ))}
      </ul>
      <ul className="nav">
        <li aria-current="page">
          Leftovers <span className="count" aria-label={`${notInUse} not in use`}>{notInUse}</span>
        </li>
      </ul>

      {menu && (
        <ContextMenu
          x={menu.x}
          y={menu.y}
          label={`Actions for ${worktreeName(menu.worktree)}`}
          onClose={() => setMenu(null)}
          items={worktreeActions(menu.worktree, orderTerminals(terminals, terminal), { onOpenFolder, onOpenTerminal })}
        />
      )}

      {chooser && (
        <ContextMenu
          x={chooser.x}
          y={chooser.y}
          label="Choose terminal"
          onClose={() => setChooser(null)}
          items={terminals.map((t) => ({ label: t.name, checked: t.id === terminal?.id, run: () => onOpenTerminal(state.repoRoot, t.id) }))}
        />
      )}
    </nav>
  );
}

function WorktreeEntry({ worktree: w, inUse, onMenu }: { worktree: WorktreeInfo; inUse: boolean; onMenu(event: MouseEvent<HTMLLIElement>): void }) {
  return (
    <li title={`${w.path}\nRight-click or press the menu key for actions`} tabIndex={0} aria-haspopup="menu" aria-label={worktreeName(w)} onContextMenu={onMenu}>
      <span className={`dot${dotClass(w, inUse)}`} aria-hidden="true" />
      <div className="wt">
        <span className="wt-name">{worktreeName(w)}</span>
        <span className="badges">
          {badges(w, inUse).map((badge) => (
            <span key={badge} className="badge">
              {badge}
            </span>
          ))}
        </span>
      </div>
    </li>
  );
}

// The menu key and Shift+F10 also raise contextmenu, but with no pointer position: open it under the entry instead.
function menuPosition(event: MouseEvent<HTMLLIElement>): { x: number; y: number } {
  event.preventDefault();
  const rect = event.currentTarget.getBoundingClientRect();
  if (event.clientX === 0 && event.clientY === 0) return { x: rect.left + 16, y: rect.bottom };
  return { x: event.clientX, y: event.clientY };
}

// The current terminal first, so the menu's first item does what the button does.
function orderTerminals(terminals: TerminalInfo[], terminal: TerminalInfo | null): TerminalInfo[] {
  return terminal ? [terminal, ...terminals.filter((t) => t.id !== terminal.id)] : terminals;
}

function worktreeActions(
  worktree: WorktreeInfo,
  terminals: TerminalInfo[],
  { onOpenFolder, onOpenTerminal }: Pick<Props, "onOpenFolder" | "onOpenTerminal">,
): MenuItem[] {
  // A prunable worktree's folder is gone, so there is nothing to open.
  const disabled = worktree.isPrunable;
  const open: MenuItem[] =
    terminals.length === 0
      ? [{ label: "Open terminal here", run: () => onOpenTerminal(worktree.path), disabled }]
      : terminals.map((t) => ({ label: `Open in ${t.name}`, run: () => onOpenTerminal(worktree.path, t.id), disabled }));
  return [...open, { label: "Open folder", run: () => onOpenFolder(worktree.path), disabled }];
}

function dotClass(w: WorktreeInfo, inUse: boolean): string {
  if (w.isMain) return " is-main";
  return inUse ? " is-active" : "";
}

function worktreeName(w: WorktreeInfo): string {
  if (w.branch) return w.branch;
  return w.isDetached && !w.isMain ? `${folderName(w.path)} (detached)` : folderName(w.path);
}

function badges(w: WorktreeInfo, inUse: boolean): string[] {
  const list: string[] = [];
  if (w.isMain) list.push("primary");
  if (w.isPrunable) list.push("folder gone");
  if (w.isLocked) list.push("locked");
  if (w.dirtyFileCount) list.push(`${w.dirtyFileCount} changed`);
  else if (inUse && !w.isLocked) list.push("active");
  return list;
}
