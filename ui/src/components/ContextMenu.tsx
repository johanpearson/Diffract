import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from "react";
import { placeMenu, type MenuPlacement } from "./menuPlacement";

export interface MenuItem {
  label: string;
  run(): void;
  disabled?: boolean;
  /** Set on a choice among several, such as which terminal is current. Makes the item a radio item. */
  checked?: boolean;
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

/** How far each arrow key moves the focus. */
const arrowSteps = new Map([
  ["ArrowDown", 1],
  ["ArrowUp", -1],
]);

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

  const close = () => {
    onCloseRef.current();
    if (opener.current instanceof HTMLElement) opener.current.focus();
  };

  // Measured before the browser paints, so a menu opened near an edge never shows cut off first.
  const [placement, setPlacement] = useState<MenuPlacement>({ left: x, top: y });
  useLayoutEffect(() => {
    const box = menu.current!.getBoundingClientRect();
    setPlacement(placeMenu(x, y, box, { width: window.innerWidth, height: window.innerHeight }));
  }, [x, y, items.length]);

  useEffect(() => {
    enabledButtons(menu.current)[0]?.focus();
    const outside = (event: MouseEvent) => {
      if (!menu.current?.contains(event.target as Node)) onCloseRef.current();
    };
    document.addEventListener("mousedown", outside);
    return () => document.removeEventListener("mousedown", outside);
  }, []);

  return (
    <ul
      ref={menu}
      className="context-menu"
      role="menu"
      aria-label={label}
      style={placement}
      onKeyDown={(event) => onMenuKey(event, enabledButtons(menu.current), close)}
    >
      {items.map((item) => (
        <MenuEntry key={item.label} item={item} onChosen={close} />
      ))}
    </ul>
  );
}

function MenuEntry({ item, onChosen }: { item: MenuItem; onChosen(): void }) {
  const isRadio = item.checked !== undefined;
  return (
    <li role="none">
      <button
        role={isRadio ? "menuitemradio" : "menuitem"}
        aria-checked={item.checked}
        disabled={item.disabled}
        onClick={() => {
          onChosen();
          item.run();
        }}
      >
        {isRadio && <span className="check" aria-hidden="true">{item.checked ? "✓" : ""}</span>}
        {item.label}
      </button>
    </li>
  );
}

function enabledButtons(menu: HTMLUListElement | null): HTMLButtonElement[] {
  return [...(menu?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? [])];
}

/** Arrow keys move between the items, round the end. Escape or Tab closes the menu. */
function onMenuKey(event: KeyboardEvent, list: HTMLButtonElement[], close: () => void): void {
  const step = arrowSteps.get(event.key);
  if (step !== undefined) {
    event.preventDefault();
    const index = list.indexOf(document.activeElement as HTMLButtonElement);
    list[(index + step + list.length) % list.length]?.focus();
  } else if (event.key === "Escape" || event.key === "Tab") {
    event.preventDefault();
    close();
  }
}
