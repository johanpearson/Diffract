# Milestone 3: history graph

**Status:** draft, waiting for Johan's decisions in phase 0
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
- **Commit details** on the right: subject and body, author and committer with dates, parents (clickable), the refs that point to it, and the list of changed files.
- **Toggles:** "Fade merged branches" and "Agent work only", using the attribution hints from milestone 1.
- **Keyboard:** up and down move the selection, Page Up and Page Down, Home and End, Enter shows details.
- **Live:** when refs change, new commits appear at the top without losing the scroll position or the selection.

## Phase 0: spike and decisions

1. Measure, read-only, on Johan's busiest repo: how long `git log` takes for the first 500 commits and for all of them, and how many commits and refs it has.
2. Build a fixture repo with 50,000 commits and many branches and merges with `git fast-import`, in a temp folder, for the performance tests.
3. Settle the decisions below.

**Checkpoint:** Johan decides the open questions below.

### Decisions for Johan

1. **Which refs the graph shows by default.** Proposal: local branches, remote-tracking branches, tags, and every worktree's HEAD. Hidden: agent checkpoint refs (`refs/copilot/checkpoints/**` and the like) and the stash, each behind a toggle. Git Extensions shows all branches by default, so this matches it.
2. **Commit order.** Proposal: `--topo-order`, so a branch's commits stay together and the lanes are easy to follow. The alternative is `--date-order`, which interleaves branches by commit date.
3. **Seeing a commit's diff.** The brief puts Monaco diffs in milestone 4, with changes and commit. But looking at what a commit changed is a big part of daily use. Proposal: bring the read-only diff of a selected commit's file into this milestone. That needs `monaco-editor`, which is not on the pre-approved list yet; Monaco itself is a locked decision in the brief. The alternative is a file list only, with diffs coming in milestone 4.
4. **Finding a commit.** Proposal: a simple find box for hash, subject and author, over the commits already loaded. Searching the whole history with `git log --grep` comes later.

## Phase 1: reading history in the sidecar

- One streamed `git log` call per page, with `-z` and a `--format` that has explicit separators: hash, parents, author, committer, dates, subject. Refs come from the snapshot milestone 1 already builds, not from `--decorate`.
- The first page (proposal: 500 commits) comes back as fast as git can produce it. Later pages are requested as the user scrolls, so a 50,000-commit repo never sends everything at once.
- Commit details with one call: body, committer and changed files (`git show` or `git diff-tree` with `-z`).
- Tests: parsers against captured real output in `Samples/`, including merges, octopus merges, root commits, non-ASCII names and subjects, and empty subjects. Call budget tests per page and per details request.

## Phase 2: lane layout

- A pure function in TypeScript from the commits to lanes: for each row, its lane, its colour and the lines to its parents. It runs in the UI, next to the renderer that uses it, and grows page by page.
- Tests: small hand-made graphs with known layouts (linear, branch and merge, criss-cross merges, octopus, many parallel branches, a branch whose parent is not loaded yet). A performance test: lanes for 50,000 commits stay under a time budget set in phase 0.

## Phase 3: the Canvas renderer

- Canvas 2D with virtualized rows: only visible rows are drawn, and text uses the DOM next to it so it stays crisp and selectable. Dark and light theme follow Windows, with the colours from the visual plan.
- Selection, keyboard and the details panel.
- Tests: Vitest for the details panel and selection. Playwright for scrolling, selecting and the keyboard, against a fake sidecar with a large generated history.

## Phase 4: history as the start view

- `repo.open` answers with what history needs as soon as it has it. The leftover scan, `fsck` included, keeps running and its result arrives later as `repo.changed`. This changes milestone 2's protocol, and the contract samples change with it.
- The tabs: History first, Worktrees and agents second.
- Tests: opening a repo shows history before the leftover scan has finished. A call budget for what opening waits for.

## Phase 5: live history and the toggles

- When refs change, only the new commits are read (`git log <new tips> --not <old tips>`) and added at the top. The scroll position and the selection stay.
- "Fade merged branches" and "Agent work only".
- Tests: fixture repos where a branch gets a commit, a branch is added, and a branch is force-moved. Each updates the graph, with a call budget.

## Phase 6: real-world check

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
- [ ] Johan has used it on his busiest repos and signs off.

## Out of scope for milestone 3

Staging, committing, pull and push (milestone 4), cleanup (milestone 5), searching the whole history, blame, and file history.
