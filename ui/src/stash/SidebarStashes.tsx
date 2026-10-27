import { useState } from "react";
import { createPortal } from "react-dom";
import { CleanupDialog } from "../cleanup/CleanupDialog";
import { ContextMenu, type MenuItem } from "../components/ContextMenu";
import { menuPosition } from "../components/menuPosition";
import { SidebarSection } from "../components/SidebarSection";
import type { AppHost } from "../host";
import type { HistoryRef } from "../protocol";
import { stashesOf, stashLabel } from "./stashes";
import type { StashActions } from "./useStashActions";

export interface StashTarget {
  /** The worktree a stash is applied to, by the name the menu gives it, such as its branch. */
  name: string;
  /** Why an agent seems to be working there, or null. Applying is then left to Changes, which asks first. */
  inUse: string | null;
}

export interface SidebarStashesProps {
  refs: readonly HistoryRef[];
  storage: AppHost["storage"];
  actions: StashActions;
  target: StashTarget;
  /** Shows the stash in History, with Stashes turned on. */
  onShow(commitId: string): void;
}

/**
 * The repo's stashes, newest first, closed by default. Clicking one shows it in History; right-clicking applies or
 * drops it. Stashes are listed once the scan after opening has read them.
 */
export function SidebarStashes({ refs, storage, actions, target, onShow }: SidebarStashesProps) {
  const stashes = stashesOf(refs);
  const [menu, setMenu] = useState<{ stash: HistoryRef; x: number; y: number } | null>(null);
  return (
    <>
      <SidebarSection id="stashes" title="Stashes" count={stashes.length} initial={false} storage={storage}>
        <ul className="refs" aria-label="Stashes">
          {stashes.map((stash) => (
            <li key={stash.targetId}>
              <button
                className="ref-entry stash"
                aria-haspopup="menu"
                title={`${stash.name}: ${stash.message ?? ""}\nClick to show it in History.\nRight-click to apply or drop it.`}
                onClick={() => onShow(stash.targetId)}
                onContextMenu={(event) => setMenu({ stash, ...menuPosition(event) })}
              >
                <span className="ref-icon" aria-hidden="true" />
                <span className="ref-name">{stashLabel(stash)}</span>
              </button>
            </li>
          ))}
        </ul>
      </SidebarSection>

      {menu && (
        <ContextMenu
          x={menu.x}
          y={menu.y}
          label={`Actions for stash ${stashLabel(menu.stash)}`}
          onClose={() => setMenu(null)}
          items={stashMenu(menu.stash, target, actions, onShow)}
        />
      )}
      {/* On the page itself, not in the sidebar's frame, so the overlay covers the whole window. */}
      {actions.plan &&
        createPortal(<CleanupDialog plan={actions.plan} onConfirm={actions.confirm} onCancel={actions.cancel} wording={{ title: "Drop this stash?", confirm: "Drop" }} />, document.body)}
    </>
  );
}

/** The right-click menu of a stash. Apply goes to the worktree Changes shows, which the label names. */
export function stashMenu(stash: HistoryRef, target: StashTarget, actions: StashActions, onShow: (commitId: string) => void): MenuItem[] {
  const blocked = target.inUse !== null;
  const where = blocked ? `${target.name} (an agent is working there)` : target.name;
  return [
    { label: "Show in History", run: () => onShow(stash.targetId) },
    { label: `Apply to ${where}`, disabled: blocked, run: () => actions.apply(stash) },
    { label: `Apply to ${where} and drop`, disabled: blocked, run: () => actions.applyAndDrop(stash) },
    { label: "Drop…", run: () => actions.drop(stash) },
  ];
}
