/** A size in viewport pixels. */
export interface Size {
  width: number;
  height: number;
}

/** Where a menu goes, and how tall it may be when even the whole window is too short for it. */
export interface MenuPlacement {
  left: number;
  top: number;
  maxHeight?: number;
}

/** The gap kept between a menu and the window's edge. */
export const EdgeGap = 4;

/**
 * Places a menu opened at a point so all of it is inside the window. It opens down and to the right, as usual. Near
 * the bottom it opens up from the point instead, and near the right edge it opens to the left. When neither way fits,
 * it is pushed back inside the window, and a menu taller than the window gets a scroll bar.
 */
export function placeMenu(x: number, y: number, menu: Size, view: Size): MenuPlacement {
  const room = view.height - 2 * EdgeGap;
  const height = Math.min(menu.height, room);
  return {
    left: along(x, menu.width, view.width),
    top: along(y, height, view.height),
    ...(menu.height > room ? { maxHeight: room } : {}),
  };
}

/** One axis: after the point if it fits, before it if that fits, otherwise as far in as it goes. */
function along(point: number, length: number, view: number): number {
  const end = view - EdgeGap;
  if (point + length <= end) return point;
  if (point - length >= EdgeGap) return point - length;
  return Math.max(EdgeGap, end - length);
}
