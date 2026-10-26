import { describe, expect, it } from "vitest";
import { EdgeGap, placeMenu } from "./menuPlacement";

const view = { width: 1200, height: 800 };
const menu = { width: 200, height: 300 };

describe("placeMenu", () => {
  it("opens down and to the right of the point when there is room", () => {
    expect(placeMenu(100, 100, menu, view)).toEqual({ left: 100, top: 100 });
  });

  it("opens up from the point near the bottom of the window", () => {
    expect(placeMenu(100, 700, menu, view)).toEqual({ left: 100, top: 400 });
  });

  it("opens to the left of the point near the right edge", () => {
    expect(placeMenu(1100, 100, menu, view)).toEqual({ left: 900, top: 100 });
  });

  it("opens up and to the left in the bottom right corner", () => {
    expect(placeMenu(1190, 790, menu, view)).toEqual({ left: 990, top: 490 });
  });

  it("is pushed inside the window when it fits neither above nor below the point", () => {
    const short = { width: 1200, height: 500 };
    expect(placeMenu(100, 250, menu, short)).toEqual({ left: 100, top: short.height - EdgeGap - menu.height });
  });

  it("fits exactly against the edge", () => {
    expect(placeMenu(100, view.height - EdgeGap - menu.height, menu, view).top).toBe(view.height - EdgeGap - menu.height);
  });

  it("scrolls a menu taller than the window, from the top edge", () => {
    expect(placeMenu(100, 150, { width: 200, height: 900 }, view)).toEqual({ left: 100, top: EdgeGap, maxHeight: view.height - 2 * EdgeGap });
  });
});
