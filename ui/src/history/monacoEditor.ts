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

/** A button on its own line just above a change, such as "Stage hunk". */
export interface ChangeAction {
  /** The line in the right-hand side the change starts at, 1-based. The button goes above it. */
  line: number;
  label: string;
  /** Also its accessible name, so it says which change it acts on. */
  title: string;
  run(): void;
}

/** A read-only view of a changed file: a diff, or just the file when it was added or deleted. */
export interface FileView {
  show(sides: FileSides): void;
  setLayout(layout: DiffLayout): void;
  setDark(dark: boolean): void;
  /** Moves to the next or previous change. Does nothing when only one side exists. */
  goToChange(target: "next" | "previous"): void;
  /**
   * Puts a button above each change, replacing any before. Only a diff of two sides has them. They are for the mouse:
   * Monaco hides its zones from screen readers, so the same actions must also be offered outside the editor.
   */
  setActions(actions: readonly ChangeAction[]): void;
  /** Scrolls a line of the right-hand side into the middle of the view. */
  revealLine(line: number): void;
  dispose(): void;
}

const shared: monaco.editor.IEditorOptions = { readOnly: true, automaticLayout: true, minimap: { enabled: false }, scrollBeyondLastLine: false };

let models = 0;

export function createFileView(container: HTMLElement, initialDark: boolean, initialLayout: DiffLayout): FileView {
  let layout = initialLayout;
  // Monaco's theme is global, and creating an editor sets it, so a new editor must get the current one.
  let dark = initialDark;
  let shown: Shown | null = null;
  let actions: readonly ChangeAction[] = [];

  return {
    show({ path, original, modified }) {
      shown?.dispose();
      shown = original !== null && modified !== null ? showDiff(path, original, modified) : showSingle(path, modified ?? original ?? "");
      shown.setActions(actions);
    },
    setActions(value) {
      actions = value;
      shown?.setActions(value);
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
    revealLine: (line) => shown?.editor.revealLineInCenter(line),
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
    const zones = new ActionZones(diff.getModifiedEditor());
    return {
      diff,
      editor: diff.getModifiedEditor(),
      setActions: (value) => zones.set(value),
      dispose: () => (first.dispose(), zones.set([]), diff.dispose(), sides.forEach((m) => m.dispose())),
    };
  }

  function showSingle(path: string, text: string): Shown {
    const file = model(text, path);
    const editor = monaco.editor.create(container, { ...shared, model: file, theme: themeOf(dark) });
    return { diff: null, editor, setActions: () => {}, dispose: () => (editor.dispose(), file.dispose()) };
  }
}

interface Shown {
  diff: monaco.editor.IStandaloneDiffEditor | null;
  /** The editor that shows the file, or the right-hand side of a diff. */
  editor: monaco.editor.ICodeEditor;
  setActions(actions: readonly ChangeAction[]): void;
  dispose(): void;
}

/**
 * A button above each change in an editor. A view zone makes room for it as a thin empty line; the button itself is a
 * content widget in that room, because zones lie under the text layer and never get the mouse.
 */
class ActionZones {
  private zoneIds: string[] = [];
  private widgets: monaco.editor.IContentWidget[] = [];

  constructor(private readonly editor: monaco.editor.ICodeEditor) {}

  set(actions: readonly ChangeAction[]): void {
    this.widgets.forEach((w) => this.editor.removeContentWidget(w));
    this.editor.changeViewZones((zones) => {
      this.zoneIds.forEach((id) => zones.removeZone(id));
      this.zoneIds = actions.map((action) => zones.addZone({ afterLineNumber: Math.max(action.line - 1, 0), heightInPx: ZoneHeight, domNode: document.createElement("div") }));
    });
    this.widgets = actions.map((action, index) => actionWidget(action, index));
    this.widgets.forEach((w) => this.editor.addContentWidget(w));
  }
}

const ZoneHeight = 26;

let widgetIds = 0;

/** Sits just above the change's first line: in the room its zone made. */
function actionWidget(action: ChangeAction, index: number): monaco.editor.IContentWidget {
  const id = `diffract.change-action.${++widgetIds}.${index}`;
  const node = actionNode(action);
  return {
    getId: () => id,
    getDomNode: () => node,
    getPosition: () => ({ position: { lineNumber: action.line, column: 1 }, preference: [monaco.editor.ContentWidgetPositionPreference.ABOVE] }),
  };
}

function actionNode(action: ChangeAction): HTMLElement {
  const zone = document.createElement("div");
  zone.className = "change-action";
  const button = document.createElement("button");
  button.className = "abtn small";
  button.textContent = action.label;
  button.title = action.title;
  button.setAttribute("aria-label", action.title);
  // Out of the tab order: the zone is hidden from screen readers, and the bar above the diff offers the same action.
  button.tabIndex = -1;
  // The editor handles mouse downs in its zones itself; this one belongs to the button.
  button.addEventListener("mousedown", (event) => event.stopPropagation());
  button.addEventListener("click", (event) => {
    event.stopPropagation();
    action.run();
  });
  zone.append(button);
  return zone;
}

/** A model whose URI ends in the file's own name, so Monaco picks the language from its extension. */
function model(text: string, path: string): monaco.editor.ITextModel {
  return monaco.editor.createModel(text, undefined, monaco.Uri.from({ scheme: "diffract", path: `/${++models}/${path}` }));
}

function themeOf(dark: boolean): string {
  return dark ? "vs-dark" : "vs";
}
