# Discarding changes

The spike for issue 75: how to keep a file's changes before discarding them, so "Undo" can bring them back byte for byte. Checked on 2026-11-12 with Git for Windows, only in a repo created in a temp folder, with git's global and system config switched off.

## What was ruled out

- **`git stash create`.** It leaves untracked files out, takes every change in the worktree and not just the files picked, and a stash entry shows in the list agents also use.
- **A backup commit under an `archive/` tag.** Johan does not want tags for this. A hidden ref is no better: any ref outside branches, tags and remotes is listed as a checkpoint. A commit with no ref shows up as an orphaned-commit leftover.
- **A backup commit at all.** Making one through a temporary index, with `git add` and `commit-tree`, applies git's line-ending filters. With `core.autocrlf=true`, a file with mixed endings (`one\r\nTWO\nthree`) came back with every LF turned into CRLF. Switching `core.autocrlf` off for those calls fixed it, but a `.gitattributes` `eol` rule would still change the file.

## Proposal: copy the files, outside the repo

Before a discard, Diffract copies each file's bytes as they are on disk into a folder of its own under the Windows temp folder, never in the repo. The operation log line for the discard names the copies. Undo writes the bytes back.

- **Exact for any file.** No git filter touches the bytes, so CRLF, mixed endings, a missing final newline and any encoding come back as they were.
- **Nothing in the repo.** No tag, no ref, no object, no leftover row, and nothing for git's own cleanup to remove.
- **A new, untracked file** is copied the same way, then deleted. Undo writes it back.
- **A hunk** copies the whole file before `git apply --reverse` undoes the one hunk. Undo restores the whole file, so it needs the file to be as the discard left it (see below).
- **Copies last until Diffract closes** (Johan's decision, 2026-11-12). The folder is emptied when the app exits and again when it starts, in case it crashed. After a restart the discard stays in past cleanups, but Undo is gone and says so. This is a safety net for "oops", not an archive.

## Undo must not overwrite newer work

Right before undo, the file on disk must be exactly what the discard left: absent for a discarded new file, otherwise equal to what `git` would write for the index (for a whole-file discard) or to the file after the hunk was reversed. Diffract keeps a hash of that state in the log. If the file changed since, the undo step is skipped and says why.

## Git commands

Only two writes are new, and both are on the changes allowlist:

- `git restore --worktree --pathspec-from-file=- --pathspec-file-nul`, for a tracked file back to its index version.
- `git apply --reverse --unidiff-zero -`, for one hunk in the working tree, with no `--cached`.

Deleting a new file and writing bytes back are not git.
