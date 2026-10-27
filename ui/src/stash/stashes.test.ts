import { describe, expect, it } from "vitest";
import type { HistoryRef } from "../protocol";
import { stashesOf, stashLabel } from "./stashes";

const stash = (name: string, message: string | null | undefined): HistoryRef => ({ name, kind: "Stash", targetId: name, agent: null, message });

describe("stashes", () => {
  it("lists only the stashes, newest first, as git numbers them", () => {
    const refs: HistoryRef[] = [stash("stash@{10}", "c"), { name: "main", kind: "Branch", targetId: "m", agent: null }, stash("stash@{2}", "b"), stash("stash@{0}", "a")];

    expect(stashesOf(refs).map((s) => s.name)).toEqual(["stash@{0}", "stash@{2}", "stash@{10}"]);
  });

  it.each([
    ["On main: Halvfärdigt", "Halvfärdigt"],
    ["WIP on feature/åäö: 1a2b3c4 Fix the cart", "1a2b3c4 Fix the cart"],
    ["On main: On main: twice", "On main: twice"],
    ["autostash", "autostash"],
  ])("shows %s as %s", (message, label) => {
    expect(stashLabel(stash("stash@{0}", message))).toBe(label);
  });

  it("shows a stash without a message by its place", () => {
    expect(stashLabel(stash("stash@{3}", null))).toBe("stash@{3}");
    expect(stashLabel(stash("stash@{3}", "On main: "))).toBe("stash@{3}");
  });
});
