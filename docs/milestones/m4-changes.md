# Milestone 4: changes and commit

**Status:** in progress. Phase 0 is done; phase 1, status and working tree diffs, is next.
**Estimate:** about 2 weeks of evenings, as in the visual plan.
**Done when:** Johan does a day of his own work in Diffract: he stages files and hunks, commits, pulls and pushes from the installed app, and stops opening Git Extensions for it. This is the checkpoint where dogfooding starts.

The Changes view shows what is different in a worktree, lets the user stage whole files or single hunks, write a commit and send it with pull and push. It was milestone 5 until Johan moved history and changes ahead of cleanup on 2026-10-21.

## Two kinds of writes

This is the first milestone where Diffract changes a repo. Cleanup, which was planned to come first, now builds on what this milestone sets up.

- **Diffract writes to real repos** when the user stages, commits, pulls or pushes. That is the product working.
- **Coding agents still never do.** The safety rules in `AGENTS.md` do not change. Every test writes only to fixture repos in a temp folder, and pull and push are tested against a bare fixture repo standing in for the remote. Agents never stage, commit, pull or push in one of Johan's repos, not even to try it. Johan does the real-world check himself, in phase 8.

## What milestone 3 taught us

- **Starting a git process is slow on Johan's laptop**, 170 to 360 ms. Status and diffs are read with as few calls as possible. Staging several files is one call, not one per file.
- **Agents work in the same repo while Johan does.** They take git's index lock while they stage and commit. Diffract must handle finding the lock taken, and must never delete it.
- **Johan likes a clean screen.** No banner or spinner unless it tells him something he needs.

## Principles

- **Diffract writes through a separate runner.** The read-only runner stays as it is. A changes runner allows only the commands in the table below and rejects everything else before a process starts, as `ReadOnlyPolicy` does today. Milestone 5's cleanup runner follows the same pattern.
- **Nothing in this milestone throws work away.** Staging, unstaging, committing, fast-forward pulls and plain pushes all leave every change somewhere. Throwing changes away is decision 4.
- **Never force.** No `--force`, no `--no-verify`, no deleting `index.lock`. When git refuses, Diffract says why in plain words and leaves the repo as it was.
- **Live.** Agents and editors change files while the view is open. Status follows the watcher from milestone 2, and what is shown is never older than the last refresh.

## Allowed write commands

A first proposal, to settle in phase 0.

| Action | Command |
|---|---|
| Stage files | `git add -- <paths>`, paths passed on standard input with `--pathspec-from-file=- --pathspec-file-nul` |
| Prepare a new file for hunk staging | `git add --intent-to-add -- <path>` |
| Unstage files | `git restore --staged`, paths as above |
| Stage or unstage a hunk | `git apply --cached` (with `--reverse` to unstage), the patch on standard input |
| Commit | `git commit -F -`, the message on standard input; `--amend` only as in decision 5 |
| Fetch | `git fetch <remote>`, never `--prune` from here (that is a setting in milestone 6) |
| Pull | `git fetch <remote>`, then `git merge --ff-only <upstream>` only when the branch is behind and not ahead, see decision 6 |
| Push | `git push --porcelain <remote> <branch>`, with `--set-upstream` on a branch's first push |
| Create a branch | `git branch <name> <commit>` |
| Switch | `git switch <branch>`, `git switch -c <name> <commit>`, `git switch --track <remote>/<branch>`, `git switch --detach <commit>`, never `--discard-changes` or `--force` |
| Create a tag | `git tag -a <name> <commit> -F -` |
| Reset the current branch | `git reset --soft <commit>` and `git reset --mixed <commit>`, never `--hard`, `--merge` or `--keep` |

Never: `--force` or `--force-with-lease` (milestone 8), `reset --hard`, `checkout`, `clean`, `stash`, `restore` without `--staged`, `rebase`, `merge` other than through `pull --ff-only`.

## Phase 0: spike and decisions

1. Measure, read-only, on Johan's busiest repo: how long `git status --porcelain=v2 -z` takes with and without untracked files, and how many changed files a busy day has.
2. Check hunk staging against fixture repos, including the awkward cases: CRLF files with `core.autocrlf`, a file without a final newline, a renamed file, a new file staged as a hunk, non-ASCII paths and content, and a hunk next to another hunk.
3. Check what git does when an agent holds `index.lock` while Diffract stages or commits, in a fixture repo.
4. Check how the working tree side of a diff is read so its line endings match what `git diff` shows, so Monaco does not mark every line as changed.
5. Settle the decisions below.

**Checkpoint:** Johan decides the open questions below before any code that writes is written. Decided on 2026-10-22.

Steps 2 to 4 were checked on 2026-10-22, in `docs/findings/changes.md`. Every case stages correctly, as long as patches are handled as bytes. They led to three changes to the allowlist above, which Johan accepted on 2026-10-22: hunks are read with `-U0` so nearby changes stage on their own, pull is a fetch followed by `merge --ff-only` so Diffract decides from data instead of an error message, and push uses `--porcelain`.

**Done** 2026-10-22. Status on Repo A takes 110 to 190 ms, little more than starting git, so the view reads the worktree it shows once per refresh, untracked files included. Reading all 10 of its worktrees takes 2.7 seconds, so the others are read only when picked.

### Decisions

Johan accepted all these proposals on 2026-10-22, in #43. Decision 11 came from his question there about a right-click menu like Git Extensions has.

1. **Which worktree.** Proposal: Changes shows the worktree the repo was opened from, with a picker for the other worktrees. A worktree that is "In use" by an agent shows a warning before anything is staged in it.
2. **Hunks or lines.** Proposal: whole files and single hunks in this milestone. Staging single lines comes later.
3. **Where the diff lives.** Proposal: the same read-only Monaco diff editor as history, with Stage hunk or Unstage hunk on each change. Side by side or inline follows the choice already remembered from history.
4. **Discarding changes.** Proposal: not in this milestone. It throws work away, so it needs the backup and undo from milestone 5 first. It moves to milestone 5 as "Discard, with undo".
5. **Amend.** Proposal: "Amend last commit" only when that commit is not on any remote-tracking branch, so it never rewrites pushed work. Otherwise it is disabled and says why.
6. **Pull.** Proposal: `--ff-only` in this milestone. When the branch has diverged, pull says so and does nothing, and the bar shows ahead and behind counts. Pull with merge or rebase needs conflict handling, which comes in milestone 7.
7. **Push.** Proposal: pushes the current branch to its upstream. A branch without one is pushed to `origin` under the same name and gets it as its upstream, and the button says so before it runs. Push asks nothing else.
8. **Hooks and signing.** Proposal: git's hooks run as they always do, and a failing hook shows its output. Signing follows the repo's config. Credentials come from Git Credential Manager, which shows its own window; Diffract never asks for a password.
9. **When the index is locked.** Proposal: try again for up to 2 seconds, then say "Another git process is using this repo, probably an agent. Try again in a moment." Nothing is retried after that without the user.
10. **Switching and creating branches.** Not in the visual plan for this milestone, but hard to do a day's work without. Proposal: "Create branch here" and "Switch to" from the history view's menu, using plain `git switch` and `git switch -c`, which refuse rather than overwrite uncommitted changes. If Johan says no, they stay out of scope and he keeps Git Extensions or a terminal for them.
11. **A right-click menu on history rows**, as in Git Extensions. It opens with a right-click, the menu key or Shift+F10, like the worktree menu from milestone 2. It shows only what is safe in this milestone, and the rest comes with the milestone that makes it safe:

    | Menu item | Milestone | Why then |
    |---|---|---|
    | Copy hash, Copy subject | 4 | read-only |
    | Create branch here, with "Switch to it" ticked | 4 | `git branch` or `git switch -c`, adds a ref and changes nothing else |
    | Switch to a branch on this commit | 4 | `git switch`, which refuses rather than overwrite uncommitted changes. For a remote-tracking branch it creates the local branch that tracks it |
    | Check out this commit (detached) | 4 | `git switch --detach`, which refuses in the same way |
    | Create tag here | 4 | `git tag -a`, adds a ref |
    | Reset current branch to here: soft or mixed | 4 | Every change stays in the working tree or the index. A confirm dialog says how many commits leave the branch, and "Undo reset" moves it back, after checking that the branch has not moved since |
    | Reset current branch to here: hard | 5 | It throws away uncommitted work, so it needs milestone 5's backup and undo, like "Discard" in decision 4 |
    | Delete branch | 5 | uses cleanup's planner, backup tag and undo |
    | Merge into current branch, Cherry-pick, Revert | 7 | each can end in conflicts |
    | Rebase current branch onto here | 8 | interactive rebase |

    Resetting a branch that is already pushed is allowed, but the dialog warns that pushing it afterwards needs a force push, which comes in milestone 8.

## Phase 1: status and working tree diffs

- Status from one `git status --porcelain=v2 -z` call per worktree, with untracked files, using the parser milestone 1 already has. Conflicted files are listed but not resolved (milestone 7).
- A file's two sides for its diff: HEAD against the index for staged changes, the index against the working tree for unstaged ones. Added, deleted, renamed, binary and large files are handled as in history.
- Live: a change in a worktree refreshes its status, debounced as in milestone 2.
- Tests: parsers against captured real output in `Samples/`, including renames, untracked folders, non-ASCII paths, and a conflicted file. Fixture tests for each kind of change. A call budget per refresh.

## Phase 2: the changes runner and staging

- `ChangesPolicy`: the allowlist above. A test per command proves it is allowed, and that everything else is rejected before a process starts.
- Stage and unstage files, in one call for any number of paths.
- Stage and unstage a hunk: the patch is built from the hunk the UI shows, applied with `git apply --cached`, and checked to still fit. If the file changed in the meantime, nothing is staged and the user is told to look again.
- Tests: every case from phase 0 step 2, in fixture repos. After each, `git diff --cached` shows exactly the hunk and nothing else. A locked index, as in decision 9.

## Phase 3: commit

- Commit with the message from the box, subject and body. Amend as in decision 5.
- A missing name or email, a failing hook or a failing signature are each shown plainly, with git's own message underneath.
- Tests: commits in fixture repos with set author and dates, a multi-line message, non-ASCII text, a failing `pre-commit` hook, amend allowed and refused, an empty commit refused.

## Phase 4: the Changes view

From the visual plan:

- A Changes tab next to History. The bar shows the branch, the worktree, ahead and behind, and Fetch, Pull and Push.
- Unstaged and Staged lists with Stage, Unstage and "Stage all". Selecting a file shows its diff, with Stage hunk or Unstage hunk on each change.
- The commit box under the lists: Commit is enabled when something is staged and the message has a subject.
- Keyboard: up and down move through the files, Space stages or unstages the selected file, Ctrl+Enter commits.
- After a commit, History shows it at the top, as for any new commit.
- Tests: Vitest for the lists and the commit box. Playwright against a fake sidecar: stage a file, stage a hunk, commit, see the lists empty.

## Phase 5: fetch, pull and push

- Fetch, pull and push as in decisions 6 and 7, against the remote of the current branch. They run in the background and can be cancelled. The bar shows they are running and what came of it.
- Tests: against a bare fixture remote: fetch with new commits, pull that fast-forwards, pull refused because the branch diverged, first push that sets the upstream, push refused because the remote moved on. A remote that cannot be reached gives a plain message within the timeout.

## Phase 6: the history menu

- The menu from decision 11, on the selected history row, with only the items for this milestone.
- "Create branch here" and "Create tag here" ask for a name, and say before anything runs if the name is taken or not valid (`git check-ref-format`).
- Reset asks soft or mixed in a confirm dialog that lists the commits leaving the branch. "Undo reset" is offered in the result, and moves the branch back only if it is still where the reset left it.
- Tests: fixture repos for each item, including switching with uncommitted changes that git refuses, a remote-tracking branch, a detached HEAD, a taken branch name, and reset then undo, ending where it started. Playwright: the menu opens by mouse and keyboard, and runs each item against the fake sidecar.

## Phase 7: protocol and contract samples

The methods grow phase by phase. Listed here so their shapes are decided together.

| Method | Params | Result |
|---|---|---|
| `changes.status` | `repoId`, worktree | branch, ahead and behind, and the files with their state |
| `changes.file` | `repoId`, worktree, path, staged or not | the two sides, as `history.file` does |
| `changes.stage` / `changes.unstage` | `repoId`, worktree, paths or one hunk | the new status |
| `changes.commit` | `repoId`, worktree, message, amend | the new commit |
| `remote.fetch` / `remote.pull` / `remote.push` | `repoId`, worktree | what happened, or why git refused |
| `branch.create` / `branch.switch` / `tag.create` | `repoId`, worktree, commit, name | the new ref, or why git refused |
| `branch.reset` / `branch.undoReset` | `repoId`, worktree, commit, soft or mixed | the old and new commit of the branch |

Status changes arrive as `repo.changed`, as they already do. Contract samples for every shape, as in milestone 2.

## Phase 8: real-world check

1. Johan installs the CI build and does a day of his own work with it: stage, commit, pull and push in his own repos.
2. Every wrong, slow or surprising thing becomes a test and a fix.

**Checkpoint:** Johan signs off, and dogfooding starts.

## Acceptance criteria

- [ ] All tests pass locally and in CI, and the installer is built on every run.
- [ ] Every write runs through the changes runner, and a test proves it rejects anything not on its allowlist.
- [ ] Every parser has tests against captured real git output.
- [ ] Staging a hunk stages exactly that hunk, proven in fixture repos for every case in phase 0.
- [ ] Nothing in this milestone forces, skips hooks, deletes a lock or throws away a change.
- [ ] Pull and push are tested against a bare fixture remote, never a real one.
- [ ] No test or agent ever writes to a repo outside a temp folder.
- [ ] Johan has worked a day in Diffract and signs off.

## Out of scope for milestone 4

Discarding changes (decision 4), staging single lines, pull with merge or rebase and resolving conflicts (milestone 7), force push (milestone 8), stashing, the history menu items decision 11 leaves to later milestones, and settings such as identity or prune on fetch (milestone 6).
