import { describe, expect, it } from "vitest";
import type { CommitEntry } from "../protocol";
import { findMatches, nextMatch } from "./find";

function commit(id: string, subject: string, authorName = "Johan Pearson", authorEmail = "johan@example.com"): CommitEntry {
  const at = "2026-10-21T09:00:00+02:00";
  return { id, parentIds: [], authorName, authorEmail, authoredAt: at, committedAt: at, subject };
}

const commits = [
  commit("9fceb02d0ae598e95dc970b74767f19372d61af8", "Merge branch 'claude/räksmörgås-fix'", "Åsa Öberg", "åsa@exempel.se"),
  commit("4b825dc642cb6eb9a060e54bf8d69288fbee4904", "Fix the cart total"),
  commit("e83c5163316f89bfbde7d9ab23ca2e25604af290", "", "GitHub Copilot", "198982749+Copilot@users.noreply.github.com"),
  commit("a1f93c2000000000000000000000000000000000", "Add cart export"),
];

describe("findMatches", () => {
  it("finds commits by the start of their hash", () => {
    expect(findMatches(commits, "4b825d")).toEqual([1]);
    expect(findMatches(commits, "9FCEB")).toEqual([0]);
  });

  it("does not match a hash in the middle", () => {
    expect(findMatches(commits, "825dc6")).toEqual([]);
  });

  it("finds commits by subject, ignoring case and spaces around the query", () => {
    expect(findMatches(commits, "  CART ")).toEqual([1, 3]);
    expect(findMatches(commits, "räksmörgås")).toEqual([0]);
  });

  it("finds commits by author name or email", () => {
    expect(findMatches(commits, "åsa")).toEqual([0]);
    expect(findMatches(commits, "copilot@users")).toEqual([2]);
  });

  it("matches nothing for an empty query", () => {
    expect(findMatches(commits, "   ")).toEqual([]);
  });
});

describe("findMatches with branch and tag names", () => {
  const names = new Map([
    [commits[1]!.id, [{ name: "main" }, { name: "origin/main" }]],
    [commits[3]!.id, [{ name: "v2.4.0" }, { name: "feature/Cart-Export" }]],
  ]);

  it("finds commits by a branch or tag on them, ignoring case", () => {
    expect(findMatches(commits, "v2.4", names)).toEqual([3]);
    expect(findMatches(commits, "cart-export", names)).toEqual([3]);
    expect(findMatches(commits, "origin/", names)).toEqual([1]);
  });

  it("lists a commit once when both its subject and a name match", () => {
    expect(findMatches(commits, "cart", names)).toEqual([1, 3]);
  });
});

describe("nextMatch", () => {
  const matches = [3, 10, 25];

  it("goes to the next match after the selection, and wraps to the first", () => {
    expect(nextMatch(matches, 3)).toBe(10);
    expect(nextMatch(matches, 11)).toBe(25);
    expect(nextMatch(matches, 25)).toBe(3);
  });

  it("goes backwards to the match before, and wraps to the last", () => {
    expect(nextMatch(matches, 10, true)).toBe(3);
    expect(nextMatch(matches, 3, true)).toBe(25);
  });

  it("starts at the first match when nothing is selected", () => {
    expect(nextMatch(matches, -1)).toBe(3);
  });

  it("is null without matches", () => {
    expect(nextMatch([], 5)).toBeNull();
  });
});
