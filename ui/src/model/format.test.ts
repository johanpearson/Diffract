import { describe, expect, it } from "vitest";
import { leftoverKinds, verdicts } from "../protocol";
import { folderName, formatAge, formatAtRisk, kindLabels, verdictLabels } from "./format";

describe("format", () => {
  it("has a label for every verdict and kind", () => {
    expect(verdicts.map((v) => verdictLabels[v])).not.toContain(undefined);
    expect(leftoverKinds.map((k) => kindLabels[k])).not.toContain(undefined);
  });

  it("shows a dash for commits at risk that could not be counted", () => {
    expect(formatAtRisk(null)).toBe("-");
    expect(formatAtRisk(0)).toBe("0");
    expect(formatAtRisk(12)).toBe("12");
  });

  it.each([
    ["2026-10-21T09:29:40+00:00", "just now"],
    ["2026-10-21T09:25:00+00:00", "5 min ago"],
    ["2026-10-21T06:30:00+00:00", "3 h ago"],
    ["2026-10-20T09:00:00+00:00", "1 day ago"],
    ["2026-10-11T09:30:00+00:00", "10 days ago"],
  ])("says how long ago %s was", (iso, expected) => {
    expect(formatAge(iso, new Date("2026-10-21T09:30:00Z"))).toBe(expected);
  });

  it("reads the folder name from a Windows path", () => {
    expect(folderName("C:\\src\\shop")).toBe("shop");
    expect(folderName("C:\\src\\shop\\.claude\\worktrees\\räksmörgås fix\\")).toBe("räksmörgås fix");
  });
});
