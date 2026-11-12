import { useEffect, useRef, useState, type ReactNode } from "react";
import type { AppInfo } from "../protocol";
import { IconButton } from "./IconButton";
import { FolderIcon, InfoIcon } from "./icons";

interface Props {
  info: AppInfo | null;
  /** Opening a repo makes no sense yet, such as while starting or when git cannot be used. */
  canOpen: boolean;
  /** Git for Windows is being updated, which blocks opening a repo. */
  updating: boolean;
  onOpen(): Promise<void>;
  /** The open repo's branch bar, next to the name. */
  children?: ReactNode;
}

/** The app name, the open repo's branch with Fetch, Pull and Push, "About" with the versions, and "Open repo…". */
export function TopBar({ info, canOpen, updating, onOpen, children }: Props) {
  return (
    <header className="topbar">
      <span className="brand">Diffract</span>
      {children}
      <span className="grow" />
      {info && <About info={info} />}
      <IconButton
        label="Open repo…"
        title={updating ? "Wait until Git for Windows is updated" : undefined}
        icon={<FolderIcon />}
        disabled={!canOpen || updating}
        onClick={() => void onOpen()}
      />
    </header>
  );
}

/** The git and sidecar versions, a click away instead of always in the bar. Escape or a click elsewhere closes it. */
function About({ info }: { info: AppInfo }) {
  const [open, setOpen] = useState(false);
  const box = useRef<HTMLSpanElement>(null);
  useEffect(() => {
    if (!open) return;
    const away = (e: Event) => {
      if (e.target instanceof Node && !box.current?.contains(e.target)) setOpen(false);
    };
    const escape = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    document.addEventListener("pointerdown", away);
    document.addEventListener("keydown", escape);
    return () => {
      document.removeEventListener("pointerdown", away);
      document.removeEventListener("keydown", escape);
    };
  }, [open]);
  return (
    <span ref={box} className="about">
      <IconButton label="About" icon={<InfoIcon />} pressed={open} onClick={() => setOpen(!open)} />
      {open && (
        <div className="about-pop" role="region" aria-label="About Diffract">
          git {info.gitVersion ?? "not found"} · sidecar {info.sidecarVersion}
        </div>
      )}
    </span>
  );
}
