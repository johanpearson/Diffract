import { describe, expect, it } from "vitest";
import type { CommandOutput } from "../protocol";
import { closesByItself, failed, finished, started, withOutput } from "./commandRun";

const out = (kind: CommandOutput["kind"], text: string, operation = "op-1"): CommandOutput => ({ operation, kind, text });

describe("a command run", () => {
  it("collects git's lines, a progress line giving way to the next", () => {
    let run = started("op-1", "fetch", "Fetch from origin", true);
    for (const o of [out("Command", "git fetch --progress origin"), out("Progress", "Receiving objects:  40%"), out("Progress", "Receiving objects:  80%"), out("Line", "Receiving objects: 100%, done."), out("Line", "From C:/remote")])
      run = withOutput(run, o);

    expect(run.lines.map((l) => l.text)).toEqual(["git fetch --progress origin", "Receiving objects: 100%, done.", "From C:/remote"]);
  });

  it("keeps a progress line when the next command starts", () => {
    let run = started("op-1", "pull", "Pull", true);
    run = withOutput(run, out("Progress", "Receiving objects: 100%"));
    run = withOutput(run, out("Command", "git merge --ff-only origin/main"));

    expect(run.lines.map((l) => l.kind)).toEqual(["Progress", "Command"]);
  });

  it("ignores lines for another run", () => {
    const run = started("op-2", "push", "Push", true);

    expect(withOutput(run, out("Line", "late", "op-1"))).toBe(run);
  });

  it("closes by itself only after a plain success", () => {
    const run = started("op-1", "switch", "Switch to main", false);
    const undo = { label: "Undo reset", run: () => {} };

    expect(closesByItself(finished(run, { message: "Switched to main.", tone: "done" }))).toBe(true);
    expect(closesByItself(finished(run, { message: "Reset main.", tone: "done", followUp: undo }))).toBe(false);
    expect(closesByItself(finished(run, { message: "Diverged.", tone: "warning" }))).toBe(false);
    expect(closesByItself(failed(run, "fatal: no"))).toBe(false);
    expect(closesByItself(run)).toBe(false);
  });

  it("says how it ended", () => {
    const run = failed(started("op-1", "commit", "Commit", false), "The pre-commit hook failed.");

    expect([run.state, run.message]).toEqual(["failed", "The pre-commit hook failed."]);
  });
});
