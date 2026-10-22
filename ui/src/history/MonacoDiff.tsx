import { useEffect, useRef, useState, type RefObject } from "react";
import { messageOf } from "../model/format";
import type { DiffEditor, DiffSides } from "./monacoEditor";

/** Loads Monaco the first time a diff is shown. Every diff after that reuses the loaded module. */
export const loadMonaco = () => import("./monacoEditor");

/** A read-only Monaco diff editor, both sides next to each other, following the Windows theme. */
export function MonacoDiff({ sides }: { sides: DiffSides }) {
  const container = useRef<HTMLDivElement>(null);
  const { editor, error } = useDiffEditor(container);

  useEffect(() => {
    editor?.show(sides);
  }, [editor, sides]);
  useFollowTheme(editor);

  if (error) return <p className="empty" role="alert">Could not load the diff editor: {error}</p>;
  return <div className="monaco-host" ref={container} data-ready={editor !== null} />;
}

/** Creates the editor in `container` once Monaco has loaded, and disposes of it when the component goes. */
function useDiffEditor(container: RefObject<HTMLDivElement | null>) {
  const [editor, setEditor] = useState<DiffEditor | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let created: DiffEditor | null = null;
    let gone = false;
    loadMonaco()
      .then(({ createDiffEditor }) => {
        if (gone || !container.current) return;
        created = createDiffEditor(container.current, prefersDark()?.matches ?? false);
        setEditor(created);
      })
      .catch((e: unknown) => setError(messageOf(e)));
    return () => {
      gone = true;
      created?.dispose();
    };
  }, [container]);

  return { editor, error };
}

/** Switches the editor between light and dark when Windows does. */
function useFollowTheme(editor: DiffEditor | null) {
  useEffect(() => {
    const query = prefersDark();
    if (!editor || !query) return;
    const follow = () => editor.setDark(query.matches);
    query.addEventListener("change", follow);
    return () => query.removeEventListener("change", follow);
  }, [editor]);
}

function prefersDark(): MediaQueryList | null {
  return typeof matchMedia === "function" ? matchMedia("(prefers-color-scheme: dark)") : null;
}
