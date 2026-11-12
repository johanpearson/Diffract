# Hard reset

How "Reset current branch to here: hard" keeps uncommitted work so Undo can bring it back. It builds on the copies described in `discard.md`.

## What a hard reset throws away

`git reset --hard <commit>` moves the branch, sets the index to the commit and rewrites every tracked file. It does three things to work that is not committed:

- Edited tracked files go back to the commit.
- Files that were only staged (new files) are removed from the index and deleted.
- An untracked file is left alone, unless the commit has a file at that path. Then git overwrites it without asking.

## What Diffract keeps

Right before the reset, Diffract reads `git status` and copies the bytes of every changed, new or renamed file into the same folder as discard copies (under the Windows temp folder, until Diffract closes). After the reset it compares each file with its copy and drops the copies of files the reset did not touch, such as untracked files. What stays is the list in the reset result, under `kept`.

What was staged is not copied. `git diff --cached --raw` gives the mode and object id of each staged path as the index had it. The objects stay in git's object store, where the index already pointed at them, so no tag, ref or new object is made. Undo writes them back with `git update-index -z --index-info`, which restores a file that has both staged and unstaged changes exactly. A path that was not staged gets mode `000000`, which removes its entry.

## Undo

Undo does nothing, and says why, unless all of this holds:

- The branch is still at the commit the reset moved it to.
- Every kept file on disk is exactly as the reset left it (compared by SHA-256).
- Every copy is still there.

Then it runs `git reset --keep <old commit>`. Unlike `--hard`, `--keep` stops instead of overwriting a change, so newer work in a file the move would touch can never be lost. After that it writes the copies back, deletes the files that did not exist before, and restores the index entries.

## Limits

- A stopped merge, rebase or cherry-pick (a conflict in the list) is refused. The conflict's three index versions cannot be put back.
- If git has cleaned the staged objects (`git gc --prune=now` between the reset and Undo), the files and the branch come back and the staging does not. Undo says so.
- Copies last until Diffract closes. After a restart the reset stays in the list and Undo says the copies are gone.

## Git commands

Three new writes, all on the changes allowlist as exact shapes: `reset --hard <commit>`, `reset --keep <commit>` and `update-index -z --index-info`. The hard reset only runs after the copies are made, and the cleanup allowlist still refuses it.
