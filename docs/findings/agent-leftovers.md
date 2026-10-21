# What agents really leave behind

Findings from milestone 1, phase 0. Collected on 2026-10-20 from two of Johan's work repos with read-only git commands. The repos are called Repo A and Repo B here because this repo is public. Commit messages and branch names Johan chose himself are left out.

Both repos run git 2.47.1 on Windows.

## Summary

- **All traces came from GitHub Copilot.** Neither repo had a `.claude/` folder or any branch, ref or worktree that looks like Claude Code. The Claude Code patterns in `diffract.json` are still unverified.
- **Copilot does not use `copilot/*` branch names locally.** It names worktrees and branches `<github-user>-<adjective>-<noun>`, for example `johanpearson-curly-waddle`. Branches are often renamed later to describe the work.
- **Copilot worktrees live outside the repo**, in `<repos folder>/copilot-worktrees/<repo name>/<worktree name>`.
- **Copilot writes checkpoint refs**, many of them: 163 refs across 10 sessions in two weeks. They live under `refs/copilot/checkpoints/`, not `refs/checkpoints/` as the spec assumed.
- **Removed worktrees can leave folders behind** that git no longer knows about.
- **Squash merges are the norm.** Branches whose PR was merged show as `[origin/...: gone]`, but most are not ancestors of main.

## Counts

| What | Repo A | Repo B |
|---|---|---|
| Linked worktrees | 8 | 2 |
| of which in `copilot-worktrees/` | 8 | 2 |
| of which detached "prewarm" worktrees | 1 | 0 |
| Folders in `copilot-worktrees/<repo>/` that git does not know about | 2 | 0 |
| Local branches (excluding main) | 8 | 5 |
| of which named `<github-user>-*` | 8 | 3 |
| of which have no worktree | 1 | 2 |
| of which have upstream `gone` | 3 | 1 |
| `refs/copilot/checkpoints/` refs | 146 | 17 |
| Copilot checkpoint sessions | 8 | 2 |
| Stashes | 0 | 1 (made by Johan, not a tool) |
| Tags | 6 releases, no `archive/` | 0 |
| `.claude/` folder | no | no |
| Unreachable commits (`fsck --unreachable --no-reflogs`) | 386 (plus 268 trees, 240 blobs), took 4 s | 0, took under 1 s |

## Copilot in detail

### Worktrees

Path shape: `<repos folder>/copilot-worktrees/<repo name>/<name>`. On this machine that is `C:/Users/JohanP/repos/copilot-worktrees/`. The folder is shared by all repos, with one subfolder per repo.

Two kinds of names:

- `<github-user>-<adjective>-<noun>`, for example `johanpearson-curly-garbanzo`. The branch starts with the same name. In 4 of 7 such worktrees in Repo A the branch was renamed later, for example the worktree `johanpearson-expert-robot` is on a branch named after the feature.
- `copilot-prewarm-<adjective>-<noun>`: a worktree with detached HEAD at main's tip, apparently kept ready for the next session. One per repo where Copilot has been used recently.

No worktree was locked or prunable.

### Orphaned worktree folders

Repo A's folder under `copilot-worktrees/` holds 2 folders that are not registered worktrees and have no `.git` file (10 and 2 entries). They look like what is left when `git worktree remove` cannot delete everything, which happens on Windows when a file is in use. git cannot see them, so a scan based only on git output will never report them.

### Branches

- Branch names follow the worktree name, so `<github-user>-*`. No local branch matched `copilot/*`. The `copilot/*` prefix is used by the Copilot coding agent that runs on GitHub, which did not run in these repos.
- 3 branches had no worktree: one with an agent-style name, one renamed agent branch and one of Johan's own.
- 4 branches had upstream `gone`, meaning the PR was merged and the remote branch deleted. Repo A merges PRs with squash, so these are usually not ancestors of main.

### Checkpoint refs

Ref layout: `refs/copilot/checkpoints/<session-id>/<sequence>/<checkpoint-id>`

- `<session-id>` and `<checkpoint-id>` are UUIDs. `<sequence>` is a 20-digit zero-padded counter starting at 1.
- Commit message: `copilot checkpoint: <session-id> @ <checkpoint-id>`.
- Sessions had between 2 and 83 checkpoints. They span minutes to four days.
- None of them were cleaned up after the session ended.

Each checkpoint commit is a snapshot of the working tree, so it never sits on a branch itself. Listing every ref as its own leftover would mean 146 rows in Repo A.

### Agent host checkpoint commits

Found on 2026-10-20 in phase 6, scanning Diffract itself after Johan used Copilot agent sessions in VS Code that day.

- One commit with no parent and no ref: `Agent host session <session-id> - baseline checkpoint`, where `<session-id>` is a UUID.
- It sits on no branch, tag or reflog, so only `git fsck --unreachable` finds it.
- It most likely comes from the VS Code Agent Host, which runs Copilot sessions. Johan confirmed he used it that day.

### Stashes

One stash in Repo B, with git's default `WIP on <branch>` message, made by Johan. No stash matched `autostash`, `claude` or `copilot`.

### Dropped checkpoint commits

Found in phase 6. Copilot deletes the checkpoint refs of older sessions itself, but the commits stay in the repo with nothing pointing to them. In Repo A, 280 of 369 orphaned commits were `copilot checkpoint: <session-id> @ <checkpoint-id>` commits from 27 sessions that no longer had any refs, and 2 more were `copilot:dirty-archive:<uuid>`. The 8 sessions that still had refs all had a checkpoint in the last 6 days.

## Claude Code

No traces in the phase 0 repos. Johan thinks Claude Code was used in them, so its leftovers may already have been cleaned up. The current patterns (`claude/*` branches, `**/.claude/worktrees/**` paths) come from the spec and have not been checked against real data.

### Found in phase 6

A third repo, used with both agents, had one Claude Code leftover: an empty folder in `.claude/worktrees/`, left after the worktree itself was removed. Its name was `<repo>-<topic>-<6 hex digits>`, and no `claude/*` branch was left. Next to the repo were two worktree folders, named `<repo>-claude` and `<repo>-copilot`, whose records in `.git/worktrees/` were gone. Who removed those records is unknown.

## Pattern set in `diffract.json`

1. Copilot worktree path pattern `**/copilot-worktrees/**`. This is the most reliable sign of a Copilot worktree, since branch names get renamed.
2. Copilot branch pattern `copilot/*` stays, for the GitHub coding agent. The `<github-user>-*` names are not added, because they depend on the user and can't be told apart from a person's own branches.
3. `checkpointRefPatterns` decides what counts as a checkpoint ref: `refs/copilot/checkpoints/**` (seen) and `refs/checkpoints/**` (from the spec, not seen).
4. Per-agent `refPatterns` is for attribution only: `refs/copilot/checkpoints/**` for Copilot.
5. `hiddenWorktreePathPatterns` lists worktrees that are never reported: `**/copilot-worktrees/*/copilot-prewarm-*`. A prewarm worktree is detached, so it has no branch name to match and is recognized by its folder.
6. `worktreeContainerPatterns` lists folders that hold only worktrees: `**/copilot-worktrees/*` and `**/.claude/worktrees`. Only these are searched for leftover folders.
7. `toolStashPatterns` unchanged. Nothing seen confirms or rules them out.
8. `remoteBranchPatterns` (added in phase 4): a merged remote-tracking branch is reported only when its name after the remote matches, by default `claude/*` and `copilot/*`. Work repos can have hundreds of merged remote branches, and listing them all would bury the agent leftovers.

9. Per-agent `commitSubjectPrefixes` (added in phase 6) is for attribution only. For GitHub Copilot: `Agent host session `, `copilot checkpoint: ` and `copilot:dirty-archive:`.
10. `checkpointCommitPrefixes` (added in phase 6) decides which orphaned commits are dropped agent checkpoints, reported as "Safe": `copilot checkpoint: ` and `copilot:dirty-archive:`.

Items 3, 5, 6, 8 and 10 are separate from the agent entries on purpose. The brief says attribution is a hint and never decides anything, so patterns that change what is reported get their own keys.

## Decisions

Johan agreed to these on 2026-10-20 when reviewing phase 0. The milestone doc is updated to match.

- **Checkpoints are grouped per session.** One leftover per `<session-id>`, with the number of refs. A session whose newest checkpoint is older than `staleAfterDays` is "Needs review". A newer session is not reported.
- **A branch whose upstream is `gone` counts as probably merged.** This catches squash merges. It is "Safe", and its unique commits are still counted as at risk so the cleanup in milestone 5 keeps a backup tag for them.
- **Prewarm worktrees are hidden** through `hiddenWorktreePathPatterns`, unless they are dirty. Dirty means someone is working in them.
- **Leftover worktree folders are reported** as "Needs review". The snapshot lists the subfolders of each folder that matches `worktreeContainerPatterns`. A subfolder that is not a registered worktree and has no `.git` entry is a leftover. Only container folders are searched, so other projects next to a repo are never touched.
- **Orphaned commits ignore the reflog and are grouped** (phase 6). Only commits that no ref and no reflog points to are reported, as one row per agent hint plus one for the rest. The first scan of Diffract listed 12 orphans, 9 of them the original commits of squash-merged PRs that git still kept in the reflog. It now shows two rows: 1 Copilot checkpoint and 2 other commits.
- **Dropped checkpoint commits are "Safe"** (phase 6), through their own `checkpointCommitPrefixes` setting, not through the agent hint.
- **Empty leftover folders are "Safe"** (phase 6). A folder with no files holds no work.
- **Forgotten worktree folders next to the repo are found** (phase 6). Only their `.git` file is read, and they are reported only when it points into this repo's worktree records. This relaxes "only container folders are searched" in a narrow way.
- **Git calls are capped instead of timed** (phase 6). Starting a git process costs 170 to 360 ms on Johan's laptop, so the acceptance criterion now caps the number of calls.
- **Claude Code patterns are only partly checked.** Phase 6 found one real Claude Code folder, in `.claude/worktrees/`, which the container pattern matches. The `claude/*` branch pattern has still not been seen in a real repo.

## Still open

- **How to clean up orphaned commits (milestone 5).** Decided on 2026-10-21: only "Keep these" for now, and rows can be hidden. See `docs/milestones/m5-cleanup.md`. Git deletes them by itself once they are older than two weeks, the next time its automatic cleanup (`git gc`) runs. Diffract could offer two actions. "Clean up now" runs that cleanup on purpose after a preview; it cannot be undone, which the brief only allows with a preview. "Keep these" saves the commits under an `archive/` tag first, as the brief already does for unmerged work. To decide when the cleanup milestone is specified.
- **Checking what a forgotten worktree folder holds (milestone 5).** Diffract cannot tell whether such a folder holds anything not in git, so it shows "Needs review" and "-" as the risk. It could compare each file with the repo's history, as was done by hand in phase 6: both forgotten folders held only a README that was already committed.

- **The other orphaned commits in Repo A.** After the dropped checkpoints, 87 remain: mostly earlier versions of commits that were later rebased or squashed, and a few from dropped stashes. They are probably left from worktrees that were removed, since removing a worktree also removes its reflog. They show as one "Needs review" row.
