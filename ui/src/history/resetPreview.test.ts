import { describe, expect, it } from "vitest";
import type { CommitEntry, HistoryRef } from "../protocol";
import { reachable, resetPreview } from "./resetPreview";

/** a ← b ← c (main), and a ← x (side). */
const entry = (id: string, parentIds: string[]): CommitEntry => ({
  id,
  parentIds,
  authorName: "A",
  authorEmail: "a@example.com",
  authoredAt: "2026-10-21T09:00:00Z",
  committedAt: "2026-10-21T09:00:00Z",
  subject: id.toUpperCase(),
});
const commits = [entry("c", ["b"]), entry("x", ["a"]), entry("b", ["a"]), entry("a", [])];
const remote = (name: string, targetId: string): HistoryRef => ({ name, kind: "RemoteBranch", targetId, agent: null });

describe("reset preview", () => {
  it("reaches through parents, and only commits the history has", () => {
    expect([...reachable(commits, ["c", "missing"])].sort()).toEqual(["a", "b", "c"]);
  });

  it("lists the commits leaving the branch, newest first", () => {
    const preview = resetPreview(commits, "main", "c", "a", []);

    expect(preview).toEqual({ leaving: [commits[0], commits[2]], arriving: 0, pushedTo: null });
  });

  it("counts the commits that come onto the branch from another line", () => {
    expect(resetPreview(commits, "main", "c", "x", []).arriving).toBe(1);
  });

  it("names the branch's own upstream when it has a leaving commit, before any other remote branch", () => {
    const refs = [remote("fork/other", "c"), remote("origin/main", "b")];

    expect(resetPreview(commits, "main", "c", "a", refs).pushedTo).toBe("origin/main");
  });

  it("names no remote branch when none has a leaving commit", () => {
    expect(resetPreview(commits, "main", "c", "b", [remote("origin/main", "b")]).pushedTo).toBeNull();
  });
});
