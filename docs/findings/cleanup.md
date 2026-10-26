# Cleaning up, stashes and pruning

The spike from milestone 5, phase 0, step 1: every command the cleanup, the stash view and pruning will run, checked before any code that writes is written. Checked on 2026-10-26 with Git for Windows 2.56.0.windows.3, only in fixture repos created in a temp folder, with git's global and system config switched off, paths with spaces and å ä ö, and a bare repo standing in for the remote.

## What works as planned

| Case | Result |
|---|---|
| Remove a clean worktree, then its branch | `git worktree remove <path>` and `git branch -D <branch>` both work with spaces and å ä ö in the path. |
| Undo a removed worktree | `git branch <name> <sha>`, then `git worktree add <path> <name>`, brings back the branch, the folder and its last commit. |
| A worktree with a modified or only an untracked file | Refused: "contains modified or untracked files". Nothing is touched. |
| A locked worktree | Refused: "cannot remove a locked working tree", with the lock's reason. |
| Local branch, delete and undo | `git branch -D`, then `git branch <name> <sha>`. |
| Remote-tracking branch, delete and undo | `git branch -d -r origin/x` removes only the local ref. `git update-ref refs/remotes/origin/x <sha>` puts it back. |
| Checkpoint refs | `git update-ref --stdin` with `delete <ref> <expected>` lines is all or nothing: when one ref has moved, git names it, says what it expected, and deletes none of them. |
| Backup tags | `git tag -a <name> <sha> -F -` makes an annotated tag with the message from standard input. `git tag -d` removes it. |
| Stash apply by commit | `git stash apply <commit>` applies the right stash after other stashes moved it down the list, new files included. |
| Stash apply that conflicts | Exit code 1, the conflicted file shows as `u UU` in `status --porcelain=v2`, and the stash is still there. |
| Stash with new files, and a message that starts with "-" | `git stash push --include-untracked -m <message>` keeps both, with the message as its own argument. |
| Prune remote branches | `git ls-remote --heads <remote>` lists what the remote has. A remote-tracking branch it no longer has is what `git remote prune --dry-run` would prune. `git branch -d -r` removes exactly that one. A remote that cannot be reached fails at once with git's reason. |

## What changes the plan

1. **`git worktree prune` removes every prunable worktree, not the one picked.** With two worktrees whose folders are gone, prune removed both. `git worktree remove <path>` removes only the one named, even when its folder is gone. Proposal: prunable worktrees are removed with `worktree remove`, and `worktree prune` leaves the cleanup allowlist.

2. **A file held open by another program can leave a worktree half removed.** With a file open for reading, as an editor or a running build keeps one, git removed the worktree's record and every other file, then stopped with exit code 255. The folder stayed with the held file in it, and git no longer knew it as a worktree. Nothing was lost, since git only removes a worktree whose files are all committed and the branch was still there, but the folder is left behind. With a file opened without sharing, git could not read it and refused before touching anything.

   Windows refuses to rename a folder while any file in it is open, whatever the sharing. Renaming the folder away and back worked when nothing was open and was refused in every case where something was. Proposal: before `worktree remove`, Diffract renames the worktree's folder and back. If Windows refuses, the step is skipped with "A program is using files in this worktree", and so is deleting its branch. If git still stops part way, exit code 255, the result says the folder was left with the files in use, the branch is kept, and the folder shows up as a leftover folder on the next scan.

3. **`git stash drop` needs `stash@{n}`, not the stash's commit.** Given the commit, git refuses: "is not a stash reference". Agents stash too, so the list can shift while the dialog is open. Proposal: right before dropping, Diffract reads the stash list, finds the stash's current `stash@{n}` by its commit, checks `git rev-parse stash@{n}` still gives that commit, and only then drops it. A stash that is no longer in the list is skipped.

4. **`git stash store` puts a stash back on top**, as `stash@{0}`, not where it was. Undo brings the work back, not its place in the list. The result says so.

5. **The stash message is an argument, not standard input.** `git stash push` has no way to read its message from standard input, so the planned `stash push -m -` would store "-" as the message. Proposal: `git stash push -m <message>` and `git stash push --include-untracked -m <message>`, passed in the argument list, which takes a message starting with "-" as it is.

6. **Without a configured identity, git makes one up.** With no `user.name` or `user.email` set, `git var GIT_COMMITTER_IDENT` still answered, with a name and email git guessed from Windows. A backup tag would carry that, not "Diffract" as decision 1 says. Proposal: Diffract reads `git config user.name` and `git config user.email`, and when either is missing it sets `GIT_COMMITTER_NAME=Diffract` and an email of `diffract@localhost` for the tag only.

## Not checked yet

- **Moving a leftover folder to the Recycle Bin.** It needs a Windows API call, not git, and Native AOT rules out the usual .NET helper. It is checked with the code that does it, in phase 2, against folders in a temp folder only.
