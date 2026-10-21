import { useState, type MouseEvent } from "react";
import type { RepoState, WorktreeInfo } from "../protocol";
import { folderName } from "../model/format";
import { ContextMenu } from "./ContextMenu";

interface Props {
  state: RepoState;
  onOpenFolder(path: string): void;
  onOpenTerminal(path: string): void;
}

/** The repo's worktrees with their state, and the Leftovers entry with the count of everything not in use. */
export function Sidebar({ state, onOpenFolder, onOpenTerminal }: Props) {
  const [menu, setMenu] = useState<{ worktree: WorktreeInfo; x: number; y: number } | null>(null);
  const inUse = new Set(state.leftovers.filter((l) => l.verdict === "InUse" && l.path).map((l) => l.path!.toLowerCase()));
  const notInUse = state.summary.total - state.summary.inUse;

  // The menu key and Shift+F10 also raise contextmenu, but with no pointer position: open it under the entry instead.
  const openMenu = (event: MouseEvent<HTMLLIElement>, worktree: WorktreeInfo) => {
    event.preventDefault();
    const fromKeyboard = event.clientX === 0 && event.clientY === 0;
    const rect = event.currentTarget.getBoundingClientRect();
    setMenu({ worktree, x: fromKeyboard ? rect.left + 16 : event.clientX, y: fromKeyboard ? rect.bottom : event.clientY });
  };

  return (
    <nav className="sidebar" aria-label="Repository">
      <div className="repo">
        <div className="repo-name">{folderName(state.repoRoot)}</div>
        <div className="repo-path" title={state.repoRoot}>
          {state.repoRoot}
        </div>
        <button className="abtn small" onClick={() => onOpenTerminal(state.repoRoot)}>
          Open terminal
        </button>
      </div>
      <h2 id="worktrees-title">Worktrees</h2>
      <ul className="worktrees" aria-labelledby="worktrees-title">
        {state.snapshot.worktrees.map((w) => (
          <li
            key={w.path}
            title={`${w.path}\nRight-click or press the menu key for actions`}
            tabIndex={0}
            aria-haspopup="menu"
            aria-label={worktreeName(w)}
            onContextMenu={(event) => openMenu(event, w)}
          >
            <span className={`dot${w.isMain ? " is-main" : inUse.has(w.path.toLowerCase()) ? " is-active" : ""}`} aria-hidden="true" />
            <div className="wt">
              <span className="wt-name">{worktreeName(w)}</span>
              <span className="badges">
                {badges(w, inUse.has(w.path.toLowerCase())).map((badge) => (
                  <span key={badge} className="badge">
                    {badge}
                  </span>
                ))}
              </span>
            </div>
          </li>
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
          items={[
            // A prunable worktree's folder is gone, so there is nothing to open.
            { label: "Open terminal here", run: () => onOpenTerminal(menu.worktree.path), disabled: menu.worktree.isPrunable },
            { label: "Open folder", run: () => onOpenFolder(menu.worktree.path), disabled: menu.worktree.isPrunable },
          ]}
        />
      )}
    </nav>
  );
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
