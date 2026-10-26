import type { MouseEvent } from "react";

/**
 * Where a right-click menu opens: at the pointer. The menu key and Shift+F10 also raise contextmenu, but with no
 * pointer position, so the menu opens under the element instead.
 */
export function menuPosition(event: MouseEvent<HTMLElement>): { x: number; y: number } {
  event.preventDefault();
  const rect = event.currentTarget.getBoundingClientRect();
  if (event.clientX === 0 && event.clientY === 0) return { x: rect.left + 16, y: rect.bottom };
  return { x: event.clientX, y: event.clientY };
}
