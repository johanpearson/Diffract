# Milestone 3: history graph

**Status:** in progress. Johan made the phase 0 decisions on 2026-10-21; the spike is still to do.
**Estimate:** about 2 weeks of evenings, as in the visual plan.
**Done when:** opening Johan's busiest repo shows its history within a second or two, scrolling stays smooth on a repo with 50,000 commits, and the graph keeps up while agents add commits.

History becomes the view a repo opens on, because using a repo comes first and cleanup second. This milestone was number 4 until Johan moved it ahead of cleanup on 2026-10-21, so Diffract gets closer to replacing Git Extensions for daily work.

Everything in this milestone is still **read-only against real repos**: `git log`, `git show` and the like. See the safety rules in `AGENTS.md`.

## What milestone 2 taught us

- **Opening a busy repo takes seconds, mostly `git fsck`.** Johan decided in milestone 2 that this is fixed here: history and status come first, and the leftover scan, `fsck` included, runs in the background and never makes the user wait.
- **Starting a git process is slow on Johan's laptop**, 170 to 360 ms. History is loaded with as few git calls as possible, streamed and paged, never one call per commit.
- **Agents write many refs.** Copilot's checkpoint refs (146 in one repo) must not turn the graph into noise.
- **Johan likes a clean screen.** No banner or spinner unless it tells him something he needs.

## The view

From the visual plan:

- **History is the start view.** "Worktrees and agents" becomes a second tab, with the leftovers count on it.
- **The graph:** one row per commit, lanes drawn with Canvas 2D (a locked decision), virtualized so only the visible rows are drawn. Each row shows the graph, branch, tag and worktree markers, the subject, the author and the age. The current branch and each worktree's HEAD are marked.
- **Commit details** on the right: subject and body, author and committer with dates, parents (clickable), the refs that point to it, and the list of changed files. Selecting a file shows its diff in a read-only Monaco diff editor, both sides next to each other.
- **Find:** a box for hash, subject and author, over the commits loaded so far. Enter jumps to the next match.
- **Toggles:** "Fade merged branches" and "Agent work only", using the attribution hints from milestone 1.
- **Keyboard:** up and down move the selection, Page Up and Page Down, Home and End, Enter shows details.
- **Live:** when refs change, new commits appear at the top without losing the scroll position or the selection.

## Phase 0: spike and decisions

1. Measure, read-only, on Johan's busiest repo: how long `git log` takes for the first 500 commits and for all of them, and how many commits and refs it has.
2. Build a fixture repo with 50,000 commits and many branches and merges with `git fast-import`, in a temp folder, for the performance tests.
3. Settle the decisions below.

**Checkpoint:** Johan decides the open questions below. Decided on 2026-10-21.

**Done** 2026-10-21. The measurements are in `docs/findings/history.md`. Repo A has only 115 commits, so a generated 50,000-commit repo is the performance test: git reads all of it in under a second. Without a commit-graph, which Diffract cannot write, `--topo-order` reads every commit before printing the first, so history is read with one streamed call instead of one call per page. The budgets: the first rows on screen within a second for 50,000 commits, and lane layout under 300 ms on a CI runner. Starting git through its `cmd\git.exe` launcher costs about twice as much as starting the real git; Johan decided that Diffract starts the real git directly, which is done in #31, and that keeping a commit-graph becomes a setting in milestone 6.

### Decisions

Johan decided these on 2026-10-21, in #30.

1. **Refs shown by default:** local branches, remote-tracking branches, tags, and every worktree's HEAD. Agent checkpoint refs (`refs/copilot/checkpoints/**` and the like) and the stash are hidden, each behind a toggle.
2. **Commit order:** `--topo-order`, so a branch's commits stay together and the lanes are easy to follow.
3. **A commit's diff comes in this milestone.** Selecting a file in the commit details shows its diff in a read-only Monaco diff editor. `monaco-editor` is added to the pre-approved npm packages in `AGENTS.md`. Staging and editing stay in milestone 4.
4. **A simple find box** for hash, subject and author, over the commits already loaded. Searching the whole history comes later.

## Phase 1: reading history in the sidecar

- One streamed `git log --topo-order` call for the whole history, with `-z` and a `--format` that has explicit separators: hash, parents, author, dates, subject. Refs come from the snapshot milestone 1 already builds, not from `--decorate`.
- The sidecar parses the output as it arrives. The first page (500 commits) goes to the UI as soon as it is parsed, and the rest follow in pages, so the UI never waits for all of it and never gets 7 MB in one message.
- Commit details with one call: body, committer and changed files (`git show` or `git diff-tree` with `-z`).
- A file's two sides for the diff, one call each (`git show <commit>^:<path>` and `git show <commit>:<path>`), with added, deleted, renamed and binary files handled. Large files are refused with a plain message instead of being sent whole.
- Tests: parsers against captured real output in `Samples/`, including merges, octopus merges, root commits, non-ASCII names and subjects, and empty subjects. Call budget tests per page and per details request.

## Phase 2: lane layout

- A pure function in TypeScript from the commits to lanes: for each row, its lane, its colour and the lines to its parents. It runs in the UI, next to the renderer that uses it, and grows page by page.
- Tests: small hand-made graphs with known layouts (linear, branch and merge, criss-cross merges, octopus, many parallel branches, a branch whose parent is not loaded yet). A performance test: lanes for 50,000 commits stay under a time budget set in phase 0.

## Phase 3: the Canvas renderer

- Canvas 2D with virtualized rows: only visible rows are drawn, and text uses the DOM next to it so it stays crisp and selectable. Dark and light theme follow Windows, with the colours from the visual plan.
- Selection, keyboard and the details panel.
- Tests: Vitest for the details panel and selection. Playwright for scrolling, selecting and the keyboard, against a fake sidecar with a large generated history.

## Phase 4: the diff and the find box

- The read-only Monaco diff editor, both sides next to each other, following the Windows theme. Monaco is loaded only when a diff is first opened, so it never slows the start.
- The find box from decision 4.
- Tests: Vitest for find. Playwright for selecting a commit, then a file, and seeing its diff. Monaco's own rendering is not tested.

## Phase 5: history as the start view

- `repo.open` answers with what history needs as soon as it has it. The leftover scan, `fsck` included, keeps running and its result arrives later as `repo.changed`. This changes milestone 2's protocol, and the contract samples change with it.
- The tabs: History first, Worktrees and agents second.
- Tests: opening a repo shows history before the leftover scan has finished. A call budget for what opening waits for.

## Phase 6: live history and the toggles

- When refs change, only the new commits are read (`git log <new tips> --not <old tips>`) and added at the top. The scroll position and the selection stay.
- "Fade merged branches", "Agent work only", and the toggles for checkpoint refs and the stash.
- Tests: fixture repos where a branch gets a commit, a branch is added, and a branch is force-moved. Each updates the graph, with a call budget.

## Phase 7: real-world check

1. Johan installs the CI build and opens his busiest repos.
2. History shows within a second or two, scrolls smoothly, and follows an agent's commits as they happen.
3. Every wrong or slow thing becomes a test and a fix.

**Checkpoint:** Johan signs off.

## Acceptance criteria

- [ ] All tests pass locally and in CI, and the installer is built on every run.
- [ ] Every parser has tests against captured real git output.
- [ ] Lane layout has tests for every graph shape listed in phase 2, and stays within its time budget for 50,000 commits.
- [ ] Scrolling a 50,000-commit history in the built UI keeps up, measured in Playwright.
- [ ] Opening a repo never waits for `git fsck`.
- [ ] New commits appear without losing the scroll position or the selection.
- [ ] Selecting a file in a commit shows its diff, and Monaco is not loaded until then.
- [ ] Johan has used it on his busiest repos and signs off.

## Dependencies

| Package | Why |
|---|---|
| `monaco-editor` | the read-only diff of a commit's file. Monaco for diffs is a locked decision in the brief. Approved by Johan on 2026-10-21. |

## Out of scope for milestone 3

Staging, committing, pull and push (milestone 4), diffs of the working tree (milestone 4), cleanup (milestone 5), searching the whole history, blame, and file history.
