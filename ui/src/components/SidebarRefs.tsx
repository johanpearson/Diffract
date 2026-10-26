import { useMemo, useState } from "react";
import type { AppHost } from "../host";
import type { HistoryRef } from "../protocol";
import { ContextMenu } from "./ContextMenu";
import { menuPosition } from "./menuPosition";
import { sidebarRefs, type RefLeaf, type RefLevel } from "./refGroups";
import { refMenu, switchTarget, type BranchContext, type RefCommands } from "./refMenu";
import { SidebarSection } from "./SidebarSection";

interface Props {
  refs: readonly HistoryRef[];
  context: BranchContext;
  commands: RefCommands;
  storage: AppHost["storage"];
}

/** What every entry needs. */
interface EntryProps {
  context: BranchContext;
  commands: RefCommands;
  storage: AppHost["storage"];
  onMenu(ref: HistoryRef, at: { x: number; y: number }): void;
}

/**
 * The repo's branches, remote branches and tags, each section and folder opening and closing as in Git Extensions.
 * Clicking a ref shows its commit in History, double-clicking a branch switches to it, and right-clicking opens its
 * menu.
 */
export function SidebarRefs({ refs, context, commands, storage }: Props) {
  const groups = useMemo(() => sidebarRefs(refs), [refs]);
  const [menu, setMenu] = useState<{ ref: HistoryRef; x: number; y: number } | null>(null);
  const entry: EntryProps = { context, commands, storage, onMenu: (ref, at) => setMenu({ ref, ...at }) };

  return (
    <>
      <SidebarSection id="branches" title="Branches" count={countOf(groups.branches)} initial storage={storage}>
        <LevelList id="branches" level={groups.branches} {...entry} />
      </SidebarSection>
      <SidebarSection id="remotes" title="Remotes" count={groups.remotes.length} initial={false} storage={storage}>
        {groups.remotes.map((remote) => (
          <SidebarSection key={remote.name} id={`remotes/${remote.name}`} title={remote.name} count={countOf(remote.level)} level="folder" initial storage={storage}>
            <LevelList id={`remotes/${remote.name}`} level={remote.level} {...entry} />
          </SidebarSection>
        ))}
      </SidebarSection>
      <SidebarSection id="tags" title="Tags" count={countOf(groups.tags)} initial={false} storage={storage}>
        <LevelList id="tags" level={groups.tags} {...entry} />
      </SidebarSection>

      {menu && (
        <ContextMenu x={menu.x} y={menu.y} label={`Actions for ${menu.ref.name}`} onClose={() => setMenu(null)} items={refMenu(menu.ref, context, commands)} />
      )}
    </>
  );
}

/** Folders first, as in Git Extensions. A folder holding the current branch starts open. */
function LevelList({ id, level, ...entry }: EntryProps & { id: string; level: RefLevel }) {
  return (
    <>
      {level.folders.map((folder) => (
        <SidebarSection
          key={folder.name}
          id={`${id}/${folder.name}`}
          title={folder.name}
          count={folder.leaves.length}
          level="folder"
          initial={folder.leaves.some((leaf) => isCurrent(leaf.ref, entry.context))}
          storage={entry.storage}
        >
          <Entries leaves={folder.leaves} {...entry} />
        </SidebarSection>
      ))}
      <Entries leaves={level.leaves} {...entry} />
    </>
  );
}

function Entries({ leaves, ...entry }: EntryProps & { leaves: RefLeaf[] }) {
  if (leaves.length === 0) return null;
  return (
    <ul className="refs">
      {leaves.map((leaf) => (
        <RefEntry key={leaf.ref.name} leaf={leaf} {...entry} />
      ))}
    </ul>
  );
}

function RefEntry({ leaf: { label, ref }, context, commands, onMenu }: EntryProps & { leaf: RefLeaf }) {
  const target = switchTarget(ref, context);
  const current = isCurrent(ref, context);
  return (
    <li>
      <button
        className={`ref-entry ${ref.kind}${current ? " is-current" : ""}`}
        aria-current={current ? "true" : undefined}
        aria-haspopup="menu"
        title={tooltip(ref, target !== null)}
        onClick={() => commands.reveal(ref.targetId)}
        onDoubleClick={() => target && commands.switchTo(target, ref.targetId)}
        onContextMenu={(event) => onMenu(ref, menuPosition(event))}
      >
        <span className="ref-icon" aria-hidden="true" />
        <span className="ref-name">{label}</span>
      </button>
    </li>
  );
}

function isCurrent(ref: HistoryRef, context: BranchContext): boolean {
  return ref.kind === "Branch" && ref.name === context.current;
}

function tooltip(ref: HistoryRef, canSwitch: boolean): string {
  const lines = [ref.name, "Click to show it in History."];
  if (canSwitch) lines.push("Double-click to switch to it.");
  lines.push("Right-click for more.");
  return lines.join("\n");
}

function countOf(level: RefLevel): number {
  return level.leaves.length + level.folders.reduce((sum, folder) => sum + folder.leaves.length, 0);
}
