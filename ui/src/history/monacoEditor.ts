// Monaco, loaded only when the first diff opens: MonacoDiff imports this module dynamically, so it lands in its own
// chunk and never slows the start. Only the editor core and syntax colouring: no language services, which a
// read-only view does not need, so the editor worker is the only worker.
import * as monaco from "monaco-editor/editor/editor.api";
import "monaco-editor/basic-languages/monaco.contribution";
// Monaco's icon font, for the folding and scroll arrows. The core does not bring it.
import "monaco-editor/features/codicon/register";
import EditorWorker from "monaco-editor/editor/editor.worker?worker";

self.MonacoEnvironment = { getWorker: () => new EditorWorker() };

/** One file's two sides. A side is null when the file does not exist there: added or deleted. */
export interface FileSides {
  /** Picks the syntax colouring by its extension. */
  path: string;
  original: string | null;
  modified: string | null;
}

export type DiffLayout = "sideBySide" | "inline";

/** A read-only view of a changed file: a diff, or just the file when it was added or deleted. */
export interface FileView {
  show(sides: FileSides): void;
  setLayout(layout: DiffLayout): void;
  setDark(dark: boolean): void;
  /** Moves to the next or previous change. Does nothing when only one side exists. */
  goToChange(target: "next" | "previous"): void;
  dispose(): void;
}

const shared: monaco.editor.IEditorOptions = { readOnly: true, automaticLayout: true, minimap: { enabled: false }, scrollBeyondLastLine: false };

let models = 0;

export function createFileView(container: HTMLElement, initialDark: boolean, initialLayout: DiffLayout): FileView {
  let layout = initialLayout;
  // Monaco's theme is global, and creating an editor sets it, so a new editor must get the current one.
  let dark = initialDark;
  let shown: Shown | null = null;

  return {
    show({ path, original, modified }) {
      shown?.dispose();
      shown = original !== null && modified !== null ? showDiff(path, original, modified) : showSingle(path, modified ?? original ?? "");
    },
    setLayout(value) {
      layout = value;
      shown?.diff?.updateOptions({ renderSideBySide: value === "sideBySide" });
    },
    setDark(value) {
      dark = value;
      monaco.editor.setTheme(themeOf(value));
    },
    goToChange: (target) => shown?.diff?.goToDiff(target),
    dispose: () => shown?.dispose(),
  };

  function showDiff(path: string, original: string, modified: string): Shown {
    const diff = monaco.editor.createDiffEditor(container, {
      ...shared,
      originalEditable: false,
      renderSideBySide: layout === "sideBySide",
      // The layout the user chose, even in a narrow window.
      useInlineViewWhenSpaceIsLimited: false,
      // Nothing to revert or act on in a commit that is already made.
      renderMarginRevertIcon: false,
      renderGutterMenu: false,
      theme: themeOf(dark),
    });
    const sides = [model(original, path), model(modified, path)] as const;
    diff.setModel({ original: sides[0], modified: sides[1] });
    // Scroll to the first change as soon as the diff is computed, once.
    const first = diff.onDidUpdateDiff(() => {
      first.dispose();
      diff.revealFirstDiff();
    });
    return { diff, dispose: () => (first.dispose(), diff.dispose(), sides.forEach((m) => m.dispose())) };
  }

  function showSingle(path: string, text: string): Shown {
    const file = model(text, path);
    const editor = monaco.editor.create(container, { ...shared, model: file, theme: themeOf(dark) });
    return { diff: null, dispose: () => (editor.dispose(), file.dispose()) };
  }
}

interface Shown {
  diff: monaco.editor.IStandaloneDiffEditor | null;
  dispose(): void;
}

/** A model whose URI ends in the file's own name, so Monaco picks the language from its extension. */
function model(text: string, path: string): monaco.editor.ITextModel {
  return monaco.editor.createModel(text, undefined, monaco.Uri.from({ scheme: "diffract", path: `/${++models}/${path}` }));
}

function themeOf(dark: boolean): string {
  return dark ? "vs-dark" : "vs";
}
