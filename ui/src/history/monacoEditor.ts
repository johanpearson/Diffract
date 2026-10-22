// Monaco, loaded only when the first diff opens: MonacoDiff imports this module dynamically, so it lands in its own
// chunk and never slows the start. Only the editor core and syntax colouring: no language services, which a
// read-only diff does not need, so the editor worker is the only worker.
import * as monaco from "monaco-editor/editor/editor.api";
import "monaco-editor/basic-languages/monaco.contribution";
// Monaco's icon font, for the folding and scroll arrows. The core does not bring it.
import "monaco-editor/features/codicon/register";
import EditorWorker from "monaco-editor/editor/editor.worker?worker";

self.MonacoEnvironment = { getWorker: () => new EditorWorker() };

/** One file's two sides. `path` picks the syntax colouring by its extension. */
export interface DiffSides {
  path: string;
  original: string;
  modified: string;
}

/** A read-only diff editor with both sides next to each other. */
export interface DiffEditor {
  show(sides: DiffSides): void;
  setDark(dark: boolean): void;
  dispose(): void;
}

let models = 0;

export function createDiffEditor(container: HTMLElement, dark: boolean): DiffEditor {
  const editor = monaco.editor.createDiffEditor(container, {
    readOnly: true,
    originalEditable: false,
    renderSideBySide: true,
    // Side by side even in a narrow window, as the brief asks.
    useInlineViewWhenSpaceIsLimited: false,
    automaticLayout: true,
    // Nothing to revert or act on in a commit that is already made.
    renderMarginRevertIcon: false,
    renderGutterMenu: false,
    minimap: { enabled: false },
    scrollBeyondLastLine: false,
    theme: themeOf(dark),
  });
  let current: monaco.editor.ITextModel[] = [];

  return {
    show({ path, original, modified }) {
      const next = [model(original, path), model(modified, path)] as const;
      editor.setModel({ original: next[0], modified: next[1] });
      current.forEach((m) => m.dispose());
      current = [...next];
    },
    setDark: (value) => monaco.editor.setTheme(themeOf(value)),
    dispose() {
      editor.dispose();
      current.forEach((m) => m.dispose());
    },
  };
}

/** A model whose URI ends in the file's own name, so Monaco picks the language from its extension. */
function model(text: string, path: string): monaco.editor.ITextModel {
  return monaco.editor.createModel(text, undefined, monaco.Uri.from({ scheme: "diffract", path: `/${++models}/${path}` }));
}

function themeOf(dark: boolean): string {
  return dark ? "vs-dark" : "vs";
}
