import { useId, useState, type ReactNode } from "react";
import type { AppHost } from "../host";

const storageKey = (key: string) => `diffract.sidebar.open.${key}`;

/** Whether a section or folder is open, remembered between runs. */
export function useOpen(storage: AppHost["storage"], key: string, initial: boolean): [boolean, () => void] {
  const [open, setOpen] = useState(() => {
    const stored = storage.get(storageKey(key));
    return stored === null ? initial : stored === "true";
  });
  const toggle = () => {
    storage.set(storageKey(key), String(!open));
    setOpen(!open);
  };
  return [open, toggle];
}

interface Props {
  /** Names it in storage, so it opens as it was left. */
  id: string;
  title: string;
  /** How many entries it has, shown next to the title. */
  count?: number;
  initial: boolean;
  storage: AppHost["storage"];
  /** "folder" for a folder inside a section: smaller, and indented. */
  level?: "section" | "folder";
  children: ReactNode;
}

/** A titled part of the sidebar that opens and closes with its title, like a section in Git Extensions. */
export function SidebarSection({ id, title, count, initial, storage, level = "section", children }: Props) {
  const [open, toggle] = useOpen(storage, id, initial);
  const body = useId();
  const Heading = level === "section" ? "h2" : "h3";
  return (
    <div className={`side-${level}`}>
      <Heading className="side-heading">
        <button className="side-toggle" aria-label={count === undefined ? title : `${title}, ${count}`} aria-expanded={open} aria-controls={body} onClick={toggle}>
          <span className="chevron" aria-hidden="true" />
          <span className="side-title">{title}</span>
          {count !== undefined && <span className="count">{count}</span>}
        </button>
      </Heading>
      <div id={body} hidden={!open}>
        {open && children}
      </div>
    </div>
  );
}
