# Milestone 4: changes and commit

**Status:** done (2026-10-26)
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
| Stage files | `git add --pathspec-from-file=- --pathspec-file-nul`, the paths on standard input |
| Prepare a new file for hunk staging | `git add --intent-to-add -- <path>` |
| Unstage files | `git restore --staged`, paths as above. Before the first commit, when there is no HEAD to restore from: `git rm --cached -q`, which keeps the files on disk |
| Stage or unstage a hunk | `git apply --cached --unidiff-zero` (with `--reverse` to unstage), the patch on standard input |
| Commit | `git commit -F -`, the message on standard input; `--amend` only as in decision 5 |
| Fetch | `git fetch <remote>`, never `--prune` from here (that is a setting in milestone 6). With `--progress` when the command window shows it (phase 8) |
| Pull | `git fetch <remote>`, then `git merge --ff-only <upstream>` only when the branch is behind and not ahead, see decision 6 |
| Push | `git push --porcelain <remote> <branch>`, with `--set-upstream` on a branch's first push, and `--progress` as for fetch |
| Create a branch | `git branch <name> <commit>` |
| Switch | `git switch <branch>`, `git switch -c <name> <commit>`, `git switch --track <remote>/<branch>`, `git switch --detach <commit>`, never `--discard-changes` or `--force` |
| Create a tag | `git tag -a <name> <commit> -F -` |
| Reset the current branch | `git reset --soft <commit>` and `git reset --mixed <commit>`, never `--hard`, `--merge` or `--keep` |

Every path is taken literally, never as a pattern: the changes runner always sets `GIT_LITERAL_PATHSPECS=1`, so a file named `*.txt` stages only itself.

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

   **Changed** by Johan on 2026-10-26, after his first day of use: Diffract is for a person working on branches, and worktrees come second. Changes leads with the current branch. The worktree picker stays, but out of the way. See phase 8.
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

**Done** 2026-10-22, in #45. A folder with a repo of its own, such as an agent's worktree inside the repo, is listed by git as one untracked folder. Johan decided on 2026-10-22 that the Changes view hides folders that are this repo's own worktrees (phase 4), and that a nested repo is never staged (phase 2).

## Phase 2: the changes runner and staging

- `ChangesPolicy`: the allowlist above. A test per command proves it is allowed, and that everything else is rejected before a process starts.
- Stage and unstage files, in one call for any number of paths.
- Stage and unstage a hunk: the patch is built from the hunk the UI shows, applied with `git apply --cached`, and checked to still fit. If the file changed in the meantime, nothing is staged and the user is told to look again.
- Tests: every case from phase 0 step 2, in fixture repos. After each, `git diff --cached` shows exactly the hunk and nothing else. A locked index, as in decision 9.

**Done** 2026-10-22, in #46. Before the first commit there is no HEAD for `restore --staged`, so unstaging uses `git rm --cached`, which keeps the files on disk; Johan merged it as proposed. A new or deleted file is staged as a whole: with no context lines it is one hunk anyway.

## Phase 3: commit

- Commit with the message from the box, subject and body. Amend as in decision 5.
- A missing name or email, a failing hook or a failing signature are each shown plainly, with git's own message underneath.
- Tests: commits in fixture repos with set author and dates, a multi-line message, non-ASCII text, a failing `pre-commit` hook, amend allowed and refused, an empty commit refused.
- Status says which remote branch already has HEAD, read side by side with status, so the view knows before a click whether amend is allowed.
- A commit may take up to 10 minutes, not the usual 30 seconds, as a `pre-commit` hook may run linters or tests.

**Done** 2026-10-23, in #47. Windows CI also found a write that failed instead of waiting when an agent let go of the index lock at just the wrong moment; that is fixed there too. Failing tests now show as annotations on the PR.

## Phase 4: the Changes view

From the visual plan:

- A Changes tab next to History. The bar shows the branch, the worktree, ahead and behind, and Fetch, Pull and Push.
- Unstaged and Staged lists with Stage, Unstage and "Stage all". Selecting a file shows its diff, with Stage hunk or Unstage hunk on each change.
- The commit box under the lists: Commit is enabled when something is staged and the message has a subject.
- Keyboard: up and down move through the files, Space stages or unstages the selected file, Ctrl+Enter commits.
- Folders that are this repo's own worktrees, such as an agent's worktree under `.claude/worktrees`, are not listed. Any other nested repo is listed without Stage.
- Each change in the diff has its Stage hunk or Unstage hunk button just above it. Monaco hides its own lines from screen readers, so the bar above the diff also goes through the changes one at a time, with the same action, for the keyboard.
- Fetch, Pull and Push join the bar in phase 5, with what they do.
- After a commit, History shows it at the top, as for any new commit.
- Tests: Vitest for the lists and the commit box. Playwright against a fake sidecar: stage a file, stage a hunk, commit, see the lists empty.

**Done** 2026-10-24, in #48. A changed submodule is listed without Stage for now, as staging one needs more than `git add`; Johan merged it as proposed.

## Phase 5: fetch, pull and push

- Fetch, pull and push as in decisions 6 and 7, against the remote of the current branch. They run in the background and can be cancelled. The bar shows they are running and what came of it.
- Tests: against a bare fixture remote: fetch with new commits, pull that fast-forwards, pull refused because the branch diverged, first push that sets the upstream, push refused because the remote moved on. A remote that cannot be reached gives a plain message within the timeout.

**Done** 2026-10-24, in #49. A branch that follows a remote branch with another name, such as `topic` following `origin/main`, is not pushed from Diffract, and the button says why; Johan merged it as proposed, so that stays a job for a terminal.

## Phase 6: the history menu

- The menu from decision 11, on the selected history row, with only the items for this milestone.
- "Create branch here" and "Create tag here" ask for a name, and say before anything runs if the name is taken or not valid (`git check-ref-format`).
- Reset asks soft or mixed in a confirm dialog that lists the commits leaving the branch. "Undo reset" is offered in the result, and moves the branch back only if it is still where the reset left it.
- Tests: fixture repos for each item, including switching with uncommitted changes that git refuses, a remote-tracking branch, a detached HEAD, a taken branch name, and reset then undo, ending where it started. Playwright: the menu opens by mouse and keyboard, and runs each item against the fake sidecar.

**Done** 2026-10-24, in #50. Johan merged both proposals: names are checked by a read-only `ref.checkName` before anything runs, with `git check-ref-format` added to the read-only allowlist, and the menu acts on the worktree the repo was opened from.

## Phase 7: protocol and contract samples

The methods grew phase by phase. This is what they settled on. Every method answers a sample in `tests/Diffract.Sidecar.Tests/Contract/`, and every method that takes params has a request sample in `Contract/requests/`, written from the sidecar's own types. The UI's tests check both against the TypeScript types, so a field the two sides name or type differently fails a test.

| Method | Params | Result |
|---|---|---|
| `changes.status` | `repoId`, `worktree` | the branch, upstream, ahead and behind, the remote branch that has HEAD, and the files with their state |
| `changes.file` | `repoId`, `worktree`, `path`, `oldPath`, `staged`, and the file's `headId` and `indexId` from `changes.status` | the two sides, each as `history.file` answers, and the hunks |
| `changes.stage` / `changes.unstage` | `repoId`, `worktree`, and either `paths` or one `hunk` (`path`, `oldPath`, `id`) | the new status |
| `changes.commit` | `repoId`, `worktree`, `message`, `amend` | the new commit and the new status |
| `remote.fetch` / `remote.pull` / `remote.push` | `repoId`, `worktree` | `outcome` (Done, UpToDate, Diverged or Rejected), a plain `message`, and the new status |
| `ref.checkName` | `repoId`, `worktree`, `kind` (Branch or Tag), `name` | why the name cannot be used, or null. Only reads |
| `branch.create` | `repoId`, `worktree`, `commit`, `name`, `switch` | the new ref and a plain `message` |
| `tag.create` | `repoId`, `worktree`, `commit`, `name`, `message` | the new ref and a plain `message` |
| `branch.switch` | `repoId`, `worktree`, and one of `branch`, `remoteBranch` or `commit` | the ref now checked out, null when detached, and a plain `message` |
| `branch.reset` | `repoId`, `worktree`, `commit`, `mode` (Soft or Mixed) | the branch, its old and new commit, and the mode |
| `branch.undoReset` | `repoId`, `worktree`, and `reset`, the result of `branch.reset` as it came | the move back, in the same shape |

Commits are always full ids. Status changes arrive as `repo.changed`, as they already do.

When git or Diffract refuses, the error's `data.kind` says why, and its message says it in plain words with git's own output under it:

| Kind | Code | When |
|---|---|---|
| `IndexLocked` | -32006 | another git process held the index for 2 seconds |
| `ChangedSinceShown` | -32007 | a hunk's file changed since its diff was read; nothing was staged |
| `CommitRefused` | -32008 | nothing was committed: no identity, nothing staged, a pushed commit to amend, or a hook or signing failed |
| `RemoteFailed` | -32009 | a fetch, pull or push could not run or finish, such as an unreachable remote |
| `ChangeRefused` | -32010 | a branch, tag, switch or reset was not made, such as a switch over uncommitted changes |

**Done** 2026-10-24, in #51.

## Phase 8: real-world check

1. Johan installs the CI build and does a day of his own work with it: stage, commit, pull and push in his own repos.
2. Every wrong, slow or surprising thing becomes a test and a fix.

### What the first day found

Johan's notes from 2026-10-26, each fixed in its own PR, with tests:

1. **Right-click menus were cut off** near the bottom of the window. They now open upwards there, and to the left near the right edge. Done in #52.
2. **"Git was not found" after updating git** from Diffract, until a restart. Git for Windows 2.56 moved git to a new folder. Diffract now looks for git again when it cannot start it. Done in #53.
3. **A sidebar like Git Extensions has.** Today it lists only worktrees, which is confusing. It becomes collapsible sections, each remembered open or closed:
   - **Branches**, the local ones, open by default. The current one is marked.
   - **Remotes**, with each remote's branches under it.
   - **Tags.**
   - **Worktrees**, closed by default, with its menu from milestone 2. Agent cleanup lands here in milestone 5.

   Clicking a branch, remote branch or tag selects its commit in History and scrolls to it. Double-clicking a branch, or "Switch to" in its right-click menu, switches to it with `git switch`, which refuses rather than overwrite uncommitted work, as in decision 10. For a remote branch it creates the local branch that tracks it. Done in #55.
4. **Fetch, Pull and Push in the top bar**, on every view, with ahead and behind counts. Until now they were only in Changes. They act on the current branch of the worktree the repo was opened from, as decisions 6 and 7 say, and they leave a worktree an agent is using alone. They are no longer in the Changes bar, so there is one place for them. Done in #60.
5. **A command window** for everything that runs git and takes more than a moment: fetch, pull, push, commit, switch, reset, and creating a branch or tag. It shows the command, git's output as it arrives, and the result in plain words.
   - It closes by itself about 1.5 seconds after a command succeeds. When git fails or warns, it stays open with the whole output until closed.
   - Each kind of command has its own short animation while it runs: arrows coming down for fetch, a branch moving forward for pull, arrows going up for push, a new dot on the line for commit, a branch swinging over for switch. With Windows' "reduce motion" setting on, it shows a still icon instead.
   - Staging and unstaging stay instant and show no window.
   - To show git's progress while it runs, fetch and push get `--progress` on the changes allowlist, and the sidecar sends git's output lines to the UI as they arrive.
   - After a reset the window stays open with "Undo reset". It replaces the notes that had to be dismissed, such as "Switched to main", which Johan asked to be rid of on 2026-10-26.

   Done in #57 and #58.
6. **Stage hunk covered the code** in the inline diff, and could not be clicked where it did. It now sits on its own line above the change. Done in #56.

**Checkpoint:** Johan signs off, and dogfooding starts.

**Done** 2026-10-26. Johan used the app, sent the fixes above, and signed off once #52 to #60 were merged.

## Acceptance criteria

- [x] All tests pass locally and in CI, and the installer is built on every run.
- [x] Every write runs through the changes runner, and a test proves it rejects anything not on its allowlist.
- [x] Every parser has tests against captured real git output.
- [x] Staging a hunk stages exactly that hunk, proven in fixture repos for every case in phase 0.
- [x] Nothing in this milestone forces, skips hooks, deletes a lock or throws away a change.
- [x] Pull and push are tested against a bare fixture remote, never a real one.
- [x] No test or agent ever writes to a repo outside a temp folder.
- [x] Johan has worked a day in Diffract and signs off.

## Out of scope for milestone 4

Discarding changes (decision 4), staging single lines, pull with merge or rebase and resolving conflicts (milestone 7), force push (milestone 8), stashing, the history menu items decision 11 leaves to later milestones, and settings such as identity or prune on fetch (milestone 6).
