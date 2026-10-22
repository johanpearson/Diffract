import { useRef, type KeyboardEvent } from "react";

export const views = ["leftovers", "history"] as const;
export type View = (typeof views)[number];

const labels: Record<View, string> = { leftovers: "Worktrees and agents", history: "History" };

export function tabId(view: View): string {
  return `tab-${view}`;
}

/** The repo's views. Left and right arrows move between them, as in any tab list. */
export function Tabs({ view, onView }: { view: View; onView(view: View): void }) {
  const list = useRef<HTMLDivElement>(null);

  const onKeyDown = (event: KeyboardEvent) => {
    const step = event.key === "ArrowRight" ? 1 : event.key === "ArrowLeft" ? -1 : 0;
    if (step === 0) return;
    const next = views[(views.indexOf(view) + step + views.length) % views.length]!;
    onView(next);
    list.current?.querySelector<HTMLElement>(`#${tabId(next)}`)?.focus();
  };

  return (
    <div className="tabs" role="tablist" aria-label="Views" ref={list} onKeyDown={onKeyDown}>
      {views.map((v) => (
        <button key={v} id={tabId(v)} role="tab" aria-selected={v === view} tabIndex={v === view ? 0 : -1} onClick={() => onView(v)}>
          {labels[v]}
        </button>
      ))}
    </div>
  );
}
