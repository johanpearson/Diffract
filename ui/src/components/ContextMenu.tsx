import { useEffect, useRef, type KeyboardEvent } from "react";

export interface MenuItem {
  label: string;
  run(): void;
  disabled?: boolean;
}

interface Props {
  /** Where it opens, in viewport pixels. */
  x: number;
  y: number;
  label: string;
  items: MenuItem[];
  /** Called when the menu closes, whether an item ran or not. Focus goes back to whatever opened it. */
  onClose(): void;
}

/**
 * A small context menu, following the WAI-ARIA menu pattern: focus moves into it, arrow keys move between items,
 * Enter runs one, Escape or Tab closes it, and so does a click anywhere else.
 */
export function ContextMenu({ x, y, label, items, onClose }: Props) {
  const menu = useRef<HTMLUListElement>(null);
  const opener = useRef<Element | null>(document.activeElement);
  // A ref, so a re-render of the parent, such as a live update, never resets focus inside the open menu.
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  const buttons = () => [...(menu.current?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? [])];

  const close = () => {
    onCloseRef.current();
    if (opener.current instanceof HTMLElement) opener.current.focus();
  };

  useEffect(() => {
    buttons()[0]?.focus();
    const outside = (event: MouseEvent) => {
      if (!menu.current?.contains(event.target as Node)) onCloseRef.current();
    };
    document.addEventListener("mousedown", outside);
    return () => document.removeEventListener("mousedown", outside);
  }, []);

  const onKeyDown = (event: KeyboardEvent) => {
    const list = buttons();
    const index = list.indexOf(document.activeElement as HTMLButtonElement);
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      const step = event.key === "ArrowDown" ? 1 : -1;
      list[(index + step + list.length) % list.length]?.focus();
    } else if (event.key === "Escape" || event.key === "Tab") {
      event.preventDefault();
      close();
    }
  };

  return (
    <ul ref={menu} className="context-menu" role="menu" aria-label={label} style={{ left: x, top: y }} onKeyDown={onKeyDown}>
      {items.map((item) => (
        <li key={item.label} role="none">
          <button
            role="menuitem"
            disabled={item.disabled}
            onClick={() => {
              close();
              item.run();
            }}
          >
            {item.label}
          </button>
        </li>
      ))}
    </ul>
  );
}
