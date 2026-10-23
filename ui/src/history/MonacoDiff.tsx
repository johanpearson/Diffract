import { useEffect, useRef, useState, type RefObject } from "react";
import { messageOf } from "../model/format";
import type { ChangeAction, DiffLayout, FileSides, FileView } from "./monacoEditor";

/** Loads Monaco the first time a diff is shown. Every diff after that reuses the loaded module. */
export const loadMonaco = () => import("./monacoEditor");

interface Props {
  sides: FileSides;
  layout: DiffLayout;
  /** The view once Monaco has loaded, for the change buttons. */
  onView(view: FileView | null): void;
  /** A button above each change, such as "Stage hunk". None in history. */
  actions?: readonly ChangeAction[];
}

/** A read-only Monaco view of a changed file, following the Windows theme. */
export function MonacoDiff({ sides, layout, onView, actions }: Props) {
  const container = useRef<HTMLDivElement>(null);
  const { view, error } = useFileView(container, layout);

  useEffect(() => {
    view?.show(sides);
  }, [view, sides]);
  useEffect(() => {
    view?.setLayout(layout);
  }, [view, layout]);
  useEffect(() => {
    if (actions) view?.setActions(actions);
  }, [view, actions]);
  useEffect(() => onView(view), [view, onView]);
  useFollowTheme(view);

  if (error) return <p className="empty" role="alert">Could not load the diff editor: {error}</p>;
  return <div className="monaco-host" ref={container} data-ready={view !== null} />;
}

/** Creates the view in `container` once Monaco has loaded, and disposes of it when the component goes. */
function useFileView(container: RefObject<HTMLDivElement | null>, layout: DiffLayout) {
  const [view, setView] = useState<FileView | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Only the first layout matters here; later changes go through setLayout.
  const initialLayout = useRef(layout).current;

  useEffect(() => {
    let created: FileView | null = null;
    let gone = false;
    loadMonaco()
      .then(({ createFileView }) => {
        if (gone || !container.current) return;
        created = createFileView(container.current, prefersDark()?.matches ?? false, initialLayout);
        setView(created);
      })
      .catch((e: unknown) => setError(messageOf(e)));
    return () => {
      gone = true;
      created?.dispose();
    };
  }, [container, initialLayout]);

  return { view, error };
}

/** Switches the view between light and dark when Windows does. */
function useFollowTheme(view: FileView | null) {
  useEffect(() => {
    const query = prefersDark();
    if (!view || !query) return;
    const follow = () => view.setDark(query.matches);
    query.addEventListener("change", follow);
    return () => query.removeEventListener("change", follow);
  }, [view]);
}

function prefersDark(): MediaQueryList | null {
  return typeof matchMedia === "function" ? matchMedia("(prefers-color-scheme: dark)") : null;
}
