# Staging, committing and sending changes

The spike from milestone 4, phase 0: how git behaves for everything the Changes view and the history menu will do, checked before any code that writes is written. Checked on 2026-10-22 with Git for Windows 2.55.0.5, only in fixture repos created in a temp folder, with git's global and system config switched off and a bare repo standing in for the remote.

Step 1 was measured read-only on Repo A, Johan's busiest repo, with `GIT_OPTIONAL_LOCKS=0`.

## How long status takes

Repo A has 391 tracked files and 10 worktrees. Five runs each, in milliseconds, starting the real git as Diffract does:

| What | Time |
|---|---|
| `git version` (just starting git) | 170 / 128 / 93 / 99 / 96 |
| status, no untracked files | 261 / 110 / 244 / 113 / 113 |
| status, untracked folders shown as one entry | 116 / 107 / 119 / 112 / 311 |
| status, every untracked file | 168 / 135 / 177 / 125 / 193 |
| status of all 10 worktrees, one after another | 2,693 in total |

Its busiest recent day touched 148 files across its commits.

What it means:

- **Status costs little more than starting git.** One call per refresh is fine, with every untracked file listed, so a new file in a new folder can be staged on its own.
- **Only the worktree on screen is read.** Reading all 10 takes almost 3 seconds. Picking another worktree reads that one.


## Staging a hunk

The approach: read the file's diff with `git diff --no-color --no-ext-diff`, keep the file header and the one hunk the user picked, and hand that patch to `git apply --cached` on its standard input. Unstaging does the same from `git diff --cached`, with `--reverse`.

| Case | Result |
|---|---|
| Two changes far apart, stage the second | Only the second is staged |
| Two changes 4 lines apart | With 3 lines of context git makes them one hunk. With `-U0` and `git apply --unidiff-zero` they are two, and each can be staged alone |
| CRLF file, `core.autocrlf=true` | Works. The index holds LF and the patch has no CR, as git normalises both sides |
| CRLF stored in the repo, no autocrlf | Works, and the CRs survive, as long as the patch is handled as bytes |
| Mixed CRLF and LF in one file | Works, each line keeps its own ending |
| No newline at the end of the file | Works, including a hunk that adds lines after the last one |
| New, untracked file | `git diff` shows nothing for it. After `git add --intent-to-add`, its hunks stage like any other |
| Renamed file, staged, then a hunk in it | Works, and status still shows the rename |
| Spaces and å ä ö in the path and the content | Works |
| Unstage one of two staged hunks | Works with `--reverse` |
| The file changed after the diff was read | `git apply --check` refuses and the index is untouched |

What it means:

- **Patches must be handled as bytes.** Cutting a patch with a tool that reads text lines dropped the CRs and made git refuse it. In C#, a `StreamReader` reading lines does the same. The patch is cut on `\n` only and every other byte is kept.
- **Proposal: hunks come from `-U0`**, so that changes a few lines apart can be staged on their own, as they look separate in Monaco. The patch is applied with `--unidiff-zero`. That is safe here because the patch is applied to the same index it was read from, and `--check` catches the case where it is not.
- **New files need `git add --intent-to-add`** before a hunk of them can be staged. If the hunk then fails to apply, `git restore --staged` takes the file back out. `add --intent-to-add` joins the allowlist.

## The working tree side of a diff

With `core.autocrlf=true` the file on disk has CRLF and the index has LF, so comparing the raw file with the index blob marks every line as changed.

`git cat-file --filters :<path>` gives the index side as it would be written to disk, with the same line endings and filters as the working tree file. For an unchanged file, it equals the file on disk byte for byte. So:

- **Unstaged changes:** the index side from `git cat-file --filters :<path>`, the working tree side read from disk. Both are in working tree form.
- **Staged changes:** both sides with `git cat-file blob`, both in repo form, as history does today.

## When the index is locked

An agent holding `.git/index.lock` makes `git add`, `git apply --cached` and `git commit` fail with exit code 128 and "Unable to create '…/index.lock': File exists". `git status` with `GIT_OPTIONAL_LOCKS=0` still works. The lock is left alone, as it must be.

Proposal: a write that fails while the worktree's `index.lock` exists is reported as "locked", by checking the file, not by reading git's message. That leads to decision 9's retry and message.

## Exit codes do not say why git refused

| What happened | Exit code |
|---|---|
| Switch refused, it would overwrite uncommitted changes | 1 |
| Push rejected, the remote has new commits | 1 |
| A `pre-commit` hook failed | 1 |
| Commit with nothing staged | 1 |
| Pull `--ff-only` on a diverged branch | 128 |
| Fetch from a remote that cannot be reached | 128 |
| Index locked | 128 |
| No name or email | 128 |
| Branch name taken, or not valid | 128 |

So Diffract checks what it can before it runs anything, from data git gives in a machine-readable form, and shows git's own message only for what is left:

- **Nothing staged:** from status. Commit stays disabled.
- **No name or email:** `git var GIT_AUTHOR_IDENT` exits non-zero without them. Read-only.
- **Branch name:** `git check-ref-format --branch <name>` for a valid name, the refs for a taken one.
- **Amend allowed:** `git for-each-ref --contains HEAD refs/remotes` lists the remote-tracking branches that have the commit. Empty means it was never pushed.
- **Push:** `git push --porcelain` reports each ref on its own line, with `!` and the reason for a rejected one. That is the machine-readable answer, so push uses it.
- **Pull:** see the proposal below.

A failing hook is the one case left where git's own output is the answer. It is shown as it is.

## Proposal: pull as fetch, then fast-forward

Decision 6 says pull runs `git pull --ff-only`. When the branch has diverged, that fetches, then fails with exit code 128 and a hint meant for people.

Proposal: pull runs `git fetch <remote>`, reads ahead and behind from `git status --porcelain=v2 --branch` (`# branch.ab +1 -1`), and then:

- behind only: `git merge --ff-only <upstream>`, which can only fast-forward.
- ahead and behind: nothing more runs, and the bar says the branch has diverged.
- up to date: nothing more runs.

The result is the same as `pull --ff-only`, but Diffract decides from data instead of from an error message. `merge --ff-only` joins the allowlist and `pull` leaves it.

## Switch and reset

- `git switch` refuses with exit code 1 when it would overwrite an uncommitted change, and changes nothing. When it can carry the change along, it does, and the change stays in the working tree.
- `git reset --soft` to the parent leaves the commit's changes staged. `git reset --soft <old tip>` puts the branch back where it was, with no changes left over. That is "Undo reset".
