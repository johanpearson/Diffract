import { describe, expect, it } from "vitest";
import { revealSettled } from "./useRepoWork";

const commitId = "e83c5163316f89bfbde7d9ab23ca2e25604af290";

describe("revealing a commit in History", () => {
  it("is over once the commit is found", () => {
    expect(revealSettled({ commitId }, true, { done: false, version: 1 })).toBe(true);
  });

  it("gives up once the whole history is read without it", () => {
    expect(revealSettled({ commitId }, false, { done: false, version: 1 })).toBe(false);
    expect(revealSettled({ commitId }, false, { done: true, version: 1 })).toBe(true);
  });

  it("after a toggle was turned on for it, waits for the history the toggle brings", () => {
    const reveal = { commitId, afterVersion: 4 };

    expect(revealSettled(reveal, false, { done: true, version: 4 })).toBe(false);
    expect(revealSettled(reveal, false, { done: true, version: 5 })).toBe(true);
    expect(revealSettled(reveal, true, { done: true, version: 4 })).toBe(true);
  });
});
