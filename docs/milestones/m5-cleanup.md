# Milestone 5: safe cleanup with backup tags and undo

**Status:** planned. Starts after milestone 4. Johan made the phase 0 decisions on 2026-10-21; checking the commands against fixture repos is still to do.
**Estimate:** 1 to 2 weeks of evenings, as in the visual plan.
**Done when:** Johan cleans up the agent leftovers in his busiest repos from the installed app, nothing he did not choose is touched, and every removal he tries to undo comes back.

This is the first milestone where Diffract changes a repo. It turns the leftover list from milestone 2 into a list you can select from. It was milestone 3 until Johan moved history and changes ahead of it. A confirm dialog shows exactly what will happen, then Diffract does it, keeps a backup tag for work that exists nowhere else, and records every step so it can be undone.

## Two kinds of writes

- **Diffract writes to real repos** when the user confirms a cleanup. That is the product working.
- **Coding agents still never do.** The safety rules in `AGENTS.md` do not change. Every test writes only to fixture repos in a temp folder. Agents never run a cleanup against one of Johan's repos, not even to try it. Johan does the real-world check himself, in phase 7.

## Principles

These come from the brief and the visual plan.

- **Plan, then apply.** The planner turns the selected leftovers into a `CleanupPlan`: every git command, every backup tag and every folder move, in order. The dialog shows that plan. Applying runs that plan and nothing else, so the dialog and the real operation can never disagree.
- **Check before every step.** Agents keep working while the dialog is open. Each step records what it expects, such as "branch X is still at commit abc", "this worktree is still clean" or "nothing is in use". If something changed, that step and the steps that depend on it are skipped, and the result says why. Nothing is forced.
- **"In use" is never offered.** Rows with that verdict have no checkbox. A row can also become "In use" while the dialog is open, and the step check catches that.
- **Unique work gets a backup first.** When a leftover has commits at risk, except a branch whose upstream is gone (decision 8), an annotated tag under `archive/` is created before anything is removed. If the tag cannot be created, nothing of that leftover is removed.
- **Every step can be undone or is shown as final.** The operation log records each step with what it needs to reverse it. Steps that cannot be undone are marked so in the dialog before the user confirms.
- **Diffract writes through a separate runner.** The read-only runner stays as it is. A cleanup runner allows only the commands in the table below. The sidecar test changes from "never creates a writable runner" to "only the cleanup executor creates the cleanup runner, and it rejects everything else".

## What cleaning up does, per kind

A first proposal, to settle in phase 0. "Backup" applies only when the leftover has commits at risk.

| Leftover | Removed with | Backup | Undo |
|---|---|---|---|
| Worktree (clean, not locked) | `git worktree remove <path>`, then `git branch -D <branch>` | tag on the branch tip | recreate the branch, then `git worktree add <path> <branch>` |
| Prunable worktree (folder gone) | `git worktree prune` for its record | tag on its HEAD | recreate the branch from the tag; the folder was already gone |
| Local branch | `git branch -D <name>` | tag on the tip, none when its upstream is gone (decision 8) | `git branch <name> <sha>` |
| Remote-tracking branch | `git branch -d -r <remote>/<name>`, local only, never pushed | tag on the tip | `git update-ref refs/remotes/<remote>/<name> <sha>` |
| Stash | `git stash drop`, after checking its commit is still the same | tag on the stash commit | `git stash store -m <message> <sha>` |
| Checkpoint session | `git update-ref --stdin` deleting each of its refs, with their expected values | none, see decision 4 | the same command, recreating them |
| Old backup tag | `git tag -d <name>`, after checking it still points where it did | none, it is the backup | `git tag <name> <sha>`, while git still has the commits |
| Leftover folder | moved to the Windows Recycle Bin | none | restore it from the Recycle Bin |
| Orphaned commits | nothing is removed: "Keep these" only, see decision 3 | one tag holding all of them | `git tag -d` on that tag |

Allowed write commands for the cleanup runner: `worktree remove` (never `--force`), `worktree prune`, `worktree add`, `branch -D`, `branch -d -r`, `branch <name> <sha>`, `tag -a`, `tag -d`, `tag <name> <sha>`, `stash drop`, `stash store`, `update-ref` (only with expected old values). Never: `push`, `fetch`, `reset`, `checkout`, `switch`, `clean`, `gc`, `reflog expire`, or `--force` on anything.

## Phase 0: decisions

1. Settle the table above. Check every command against fixture repos first, including the awkward cases: a prunable worktree when others are prunable too, a stash that moved down the list, a worktree with a locked file on Windows, paths with spaces and å ä ö.
2. Write down the decisions below in this doc.

**Checkpoint:** Johan decides the open questions below before any code that writes is written. Decided on 2026-10-21; the command check in step 1 is still to do.

### Decisions

Johan decided these on 2026-10-21, in #28.

1. **Backup tags are annotated `archive/` tags**, as the brief says, named `archive/<kind>/<name>-<yyyy-mm-dd>`, with a message saying what was backed up and why. Annotated tags carry their own date, which the "old backup tag" rule already reads. When the repo has no name and email set, the tagger is "Diffract".
2. **The operation log lives in Diffract's own folder**, `%LOCALAPPDATA%\Diffract\oplog\`, one file per repo, keyed by the repo's path. It stays out of the repo.
3. **Orphaned commits get only "Keep these"** in this milestone, which saves them under one `archive/` tag. No `git gc`. Johan also wants to hide rows until he gets round to them, see "Hiding rows" below.
4. **Checkpoint sessions are removed without a backup tag.** The log can recreate their refs for as long as git keeps the commits.
5. **Leftover folders go to the Recycle Bin**, so Windows itself is the undo. A folder that cannot be moved, for example because a file is locked, is skipped with the reason.
6. **A worktree's branch is removed with it**, as in the visual plan, listed as its own step in the dialog with its own checkbox, ticked by default.
7. **Cleanup moved from milestone 3 to milestone 5**, after history and changes, so Diffract becomes usable for daily work sooner. See the brief.
8. **A branch whose upstream is gone gets no backup tag.** Johan decided this on 2026-10-22, after asking for a way to remove all merged local branches at once. Such a branch was most likely squash-merged, so its own commits are not on main and the rule above would tag every one. Pruning his Diffract repo's 27 gone branches would have made 27 `archive/` tags, which come back as "old backup tag" leftovers 30 days later. Undo still works from the operation log for as long as git keeps the commits, as for checkpoint sessions in decision 4. The confirm dialog says this step cannot be undone once git has removed the commits. An unmerged branch whose upstream still exists keeps its tag.

### Hiding rows

Any row can be hidden with "Hide", and the summary counts leave it out. A hidden row comes back by itself when it changes: a branch that moves, a session with new checkpoints, more orphaned commits than when it was hidden. "Show hidden" lists them again. Hidden rows are remembered in Diffract's own folder, next to the operation log. Hiding is not a cleanup: it changes nothing in the repo.

## Phase 1: the cleanup runner and the planner

- `CleanupPolicy`: the write allowlist above. A test per command proves it is allowed, and that everything else is rejected before a process starts, as `ReadOnlyPolicy` does today.
- `CleanupPlanner`: a pure function from a snapshot, the selected leftovers and the config to a `CleanupPlan`. One step list per leftover, each step with its command, its expectations, its backup and its undo.
- Tests: one fixture scenario per leftover kind, as in milestone 1. The plan for each kind matches the table. Nothing "In use" is ever planned, and a plan for a clean repo is empty.

## Phase 2: applying and the operation log

- `CleanupExecutor` checks each step's expectations right before running it, runs it through the cleanup runner, and writes the result to the operation log before moving on.
- A step that fails, or finds its expectations broken, stops that leftover. Other leftovers carry on, since they do not depend on it.
- Undo reads a cleanup from the log and runs its undo steps in reverse order, with the same checks.
- Tests: every kind is removed and then undone in a fixture repo, and the repo ends where it started (refs, worktrees, stashes). Then the awkward paths: a branch that moved after planning, a worktree that turned dirty, a locked file, an undo after git already removed the commits. A call budget per step, as in milestones 1 and 2.

## Phase 3: protocol

| Method | Params | Result |
|---|---|---|
| `cleanup.plan` | `repoId`, the selected leftovers | the plan, as the dialog shows it |
| `cleanup.apply` | `repoId`, the plan's id | what was done, skipped or failed, per step |
| `cleanup.history` | `repoId` | past cleanups from the operation log |
| `cleanup.undo` | `repoId`, a cleanup's id | the same shape as `cleanup.apply` |

A plan is kept by the sidecar and applied by id, so the UI can never apply something different from what it showed. Contract samples for every shape, as in milestone 2. Live refresh pauses while a cleanup runs, as it does during a git update, and catches up once afterwards.

## Phase 4: the view

From the visual plan:

- A checkbox on every row that is not "In use", and "Clean up N selected".
- "Select all merged branches": one click ticks every local branch that is merged or whose upstream is gone, and nothing else. The dialog then lists them all, as for any selection. This is the local prune Johan asked for on 2026-10-22.
- The confirm dialog lists every step in plain English: "Remove worktree johanpearson-curly-waddle", "Delete branch …", "Keep a backup tag archive/branch/… for 3 commits that exist nowhere else". Steps that cannot be undone say so.
- The result says what was removed, what was kept as a backup, and what was skipped and why. It offers "Undo".
- An operation log view lists past cleanups with "Undo" on each.

Tests: Vitest for the dialog and the result, against contract samples. Playwright: select, confirm, see the rows go, undo, see them come back.

## Phase 5: old backup tags and orphaned commits

Old backup tags are removed like any other leftover. Orphaned commits get "Keep these", which saves them under one `archive/` tag.

## Phase 6: hiding rows

As described under "Hiding rows" in phase 0. Tests: a hidden row stays hidden across a refresh and a restart, comes back when it changes, and is never part of a cleanup unless it is shown again.

## Phase 7: real-world check

1. Johan installs the CI build and cleans up his busiest repos: the Copilot worktrees, gone branches and checkpoint sessions milestone 1 found.
2. He undoes at least one cleanup of each kind.
3. Every surprise becomes a test and a fix.

**Checkpoint:** Johan signs off.

## Acceptance criteria

- [ ] All tests pass locally and in CI, and the installer is built on every run.
- [ ] Every cleanup command runs through the cleanup runner, and a test proves it rejects anything not on its allowlist.
- [ ] Nothing "In use" can be planned, and a step whose expectations broke is never run.
- [ ] Every leftover with commits at risk gets a backup tag before it is removed.
- [ ] Every kind of cleanup has a fixture test that removes it and undoes it, and the repo ends where it started.
- [ ] The confirm dialog and the applied cleanup come from the same plan.
- [ ] A hidden row stays hidden until it changes, and hiding never changes the repo.
- [ ] No test or agent ever writes to a repo outside a temp folder.
- [ ] Johan has cleaned up his real repos with it and signs off.

## Out of scope for milestone 5

Cleaning up on the remote (deleting branches on GitHub), automatic cleanup without confirmation, removing orphaned commits with `git gc`, and settings. Deleting branches that are not leftovers, for example several unmerged branches picked from the history view, is also out: it reuses this milestone's planner and runner, but needs its own place in a later milestone.
