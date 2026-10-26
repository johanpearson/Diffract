import { describe, expect, it, vi } from "vitest";
import type { HistoryRef } from "../protocol";
import { refMenu, switchTarget, type BranchContext } from "./refMenu";

const ref = (name: string, kind: HistoryRef["kind"]): HistoryRef => ({ name, kind, targetId: `${name}-id`, agent: null });
const context: BranchContext = { current: "main", localBranches: new Set(["main", "feature/x"]) };

describe("switchTarget", () => {
  it("switches to another branch, and not to the one checked out", () => {
    expect(switchTarget(ref("feature/x", "Branch"), context)).toEqual({ branch: "feature/x" });
    expect(switchTarget(ref("main", "Branch"), context)).toBeNull();
  });

  it("switches to the local branch of a remote branch, creating it only when it is not there", () => {
    expect(switchTarget(ref("origin/feature/x", "RemoteBranch"), context)).toEqual({ branch: "feature/x" });
    expect(switchTarget(ref("origin/fix/räksmörgås", "RemoteBranch"), context)).toEqual({ remoteBranch: "origin/fix/räksmörgås" });
    expect(switchTarget(ref("origin/main", "RemoteBranch"), context)).toBeNull();
  });

  it("never checks out a tag by double-click", () => {
    expect(switchTarget(ref("v1.0", "Tag"), context)).toBeNull();
  });

  it("switches to any branch when HEAD is detached", () => {
    expect(switchTarget(ref("main", "Branch"), { ...context, current: null })).toEqual({ branch: "main" });
  });
});

describe("refMenu", () => {
  const commands = () => ({ reveal: vi.fn(), switchTo: vi.fn(), copy: vi.fn() });
  const labels = (r: HistoryRef) => refMenu(r, context, commands()).map((i) => [i.label, i.disabled ?? false]);

  it("shows, switches and copies, saying what switching does", () => {
    expect(labels(ref("feature/x", "Branch"))).toEqual([["Show in History", false], ["Switch to feature/x", false], ["Copy name", false]]);
    expect(labels(ref("origin/new", "RemoteBranch"))[1]).toEqual(["Switch to origin/new as new branch new", false]);
    expect(labels(ref("origin/feature/x", "RemoteBranch"))[1]).toEqual(["Switch to feature/x", false]);
    expect(labels(ref("v1.0", "Tag"))[1]).toEqual(["Check out v1.0 (detached)", false]);
  });

  it("disables switching to the branch already checked out", () => {
    expect(labels(ref("main", "Branch"))[1]).toEqual(["On main already", true]);
    expect(labels(ref("origin/main", "RemoteBranch"))[1]).toEqual(["On main already", true]);
  });

  it("runs each item against the ref's commit", () => {
    const c = commands();
    const [show, switchTo, copy] = refMenu(ref("origin/new", "RemoteBranch"), context, c);

    show!.run();
    switchTo!.run();
    copy!.run();

    expect(c.reveal).toHaveBeenCalledWith("origin/new-id");
    expect(c.switchTo).toHaveBeenCalledWith({ remoteBranch: "origin/new" }, "origin/new-id");
    expect(c.copy).toHaveBeenCalledWith("origin/new", "the name");
  });

  it("checks out a tag's commit, detached, from the menu", () => {
    const c = commands();
    refMenu(ref("v1.0", "Tag"), context, c)[1]!.run();

    expect(c.switchTo).toHaveBeenCalledWith({ commit: "v1.0-id" }, "v1.0-id");
  });
});
