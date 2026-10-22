import { describe, expect, it } from "vitest";
import { moveSelection, scrollToShow, visibleRange } from "./viewport";

describe("visibleRange", () => {
  it("covers the rows in view and the overscan around them", () => {
    expect(visibleRange({ scrollTop: 280, height: 300 }, 28, 1000, 5)).toEqual({ first: 5, end: 26 });
  });

  it("starts at the first row at the top", () => {
    expect(visibleRange({ scrollTop: 0, height: 300 }, 28, 1000, 5)).toEqual({ first: 0, end: 16 });
  });

  it("stops at the last row at the bottom", () => {
    expect(visibleRange({ scrollTop: 27_700, height: 300 }, 28, 1000, 5)).toEqual({ first: 984, end: 1000 });
  });

  it("is empty when there are no rows", () => {
    expect(visibleRange({ scrollTop: 0, height: 300 }, 28, 0)).toEqual({ first: 0, end: 0 });
  });

  it("draws a few hundred rows at most for 50,000 commits", () => {
    const range = visibleRange({ scrollTop: 700_000, height: 900 }, 28, 50_000);
    expect(range.end - range.first).toBeLessThan(60);
  });
});

describe("scrollToShow", () => {
  it("leaves the scroll alone when the row is in view", () => {
    expect(scrollToShow(5, 0, 300, 28)).toBe(0);
  });

  it("scrolls up so a row above the view is at the top", () => {
    expect(scrollToShow(2, 280, 300, 28)).toBe(56);
  });

  it("scrolls down so a row below the view is at the bottom", () => {
    expect(scrollToShow(20, 0, 300, 28)).toBe(20 * 28 + 28 - 300);
  });
});

describe("moveSelection", () => {
  it.each([
    ["ArrowDown", 5, 6],
    ["ArrowUp", 5, 4],
    ["PageDown", 5, 15],
    ["PageUp", 15, 5],
    ["Home", 50, 0],
    ["End", 5, 99],
  ])("%s from row %i goes to row %i", (key, from, to) => {
    expect(moveSelection(key, from, 10, 100)).toBe(to);
  });

  it("stays within the rows", () => {
    expect(moveSelection("ArrowUp", 0, 10, 100)).toBe(0);
    expect(moveSelection("PageDown", 95, 10, 100)).toBe(99);
  });

  it("selects the first row when nothing is selected", () => {
    expect(moveSelection("ArrowDown", -1, 10, 100)).toBe(0);
    expect(moveSelection("End", -1, 10, 100)).toBe(99);
  });

  it("ignores other keys and empty lists", () => {
    expect(moveSelection("a", 5, 10, 100)).toBeNull();
    expect(moveSelection("ArrowDown", -1, 10, 0)).toBeNull();
  });
});
