#!/usr/bin/env bash
# Regenerates the parser sample files in this folder from real git output.
#
# Every repo is created inside a new temp folder that is deleted at the end.
# Nothing outside that folder is written by git. Run from Git Bash:
#   tests/Diffract.Git.Tests/Samples/capture-samples.sh
#
# The temp folder prefix is replaced with C:/samples_____ (padded to the same
# length, so long paths stay long) in the captured files. Everything else is
# byte-for-byte git output.
set -euo pipefail

out="$(cd "$(dirname "$0")" && pwd)"
tmp_unix="$(mktemp -d)"
case "$tmp_unix" in */tmp.*) ;; *) echo "mktemp gave an unexpected path: $tmp_unix" >&2; exit 1 ;; esac
trap 'rm -rf -- "$tmp_unix"' EXIT
tmp="$(cygpath -m "$tmp_unix")"
placeholder="C:/samples"
while [ ${#placeholder} -lt ${#tmp} ]; do placeholder="${placeholder}_"; done

# Isolate git from the developer's own config and make commits deterministic.
: > "$tmp/gitconfig"
export GIT_CONFIG_GLOBAL="$tmp/gitconfig" GIT_CONFIG_NOSYSTEM=1
export GIT_AUTHOR_NAME="Sample Author" GIT_AUTHOR_EMAIL="author@example.com"
export GIT_COMMITTER_NAME="Sample Author" GIT_COMMITTER_EMAIL="author@example.com"
export GIT_AUTHOR_DATE="2026-01-15T10:00:00+00:00" GIT_COMMITTER_DATE="2026-01-15T10:00:00+00:00"
export GIT_OPTIONAL_LOCKS=0 GIT_TERMINAL_PROMPT=0
git config --global core.longpaths true
git config --global core.autocrlf false

at() { export GIT_AUTHOR_DATE="$1" GIT_COMMITTER_DATE="$1"; }
commit() { echo "$2" > "$1"; git add -- "$1"; git commit -q -m "$3"; }
capture() { local name="$1"; shift; "$@" > "$out/$name"; sed -i "s|$tmp|$placeholder|g" "$out/$name"; }

refs_format='%(refname)%00%(objectname)%00%(objecttype)%00%(*objectname)%00%(upstream)%00%(upstream:track,nobracket)%00%(creatordate:unix)%00%(symref)'
stash_format='%gd%x1f%H%x1f%ct%x1f%gs'

# --- origin and main repo ---------------------------------------------------
git init -q --bare -b main "$tmp/origin.git"
git init -q -b main "$tmp/repo"
cd "$tmp/repo"
commit readme.md "hello" "Initial commit"
git remote add origin "$tmp/origin.git"
git push -q -u origin main
git remote set-head origin main
capture worktree-list-single.bin git worktree list --porcelain -z

# --- worktrees ----------------------------------------------------------------
git worktree add -q -b feature/one "$tmp/wt with spaces"
git worktree add -q -b "feature/åäö" "$tmp/wt-åäö"
git worktree add -q --detach "$tmp/wt-detached"
git worktree add -q -b locked-plain "$tmp/wt-locked-plain"
git worktree lock "$tmp/wt-locked-plain"
git worktree add -q -b locked-reason "$tmp/wt-locked-reason"
git worktree lock --reason "Claude Code session 42" "$tmp/wt-locked-reason"
git worktree add -q -b gone-folder "$tmp/wt-prunable"
rm -rf "$tmp/wt-prunable"
# git for Windows fails with "'$GIT_DIR' too big" somewhere between 211 and 226
# characters, even with core.longpaths. Aim for 210 so the sample stays 200+.
long="$tmp/long/"
while [ ${#long} -lt 190 ]; do long="${long}segment-of-a-long-path/"; done
long="${long}$(printf 'x%.0s' $(seq $((201 - ${#long}))))/worktree"
mkdir -p "$(dirname "$long")"
git worktree add -q -b long-path "$long"
capture worktree-list-many.bin git worktree list --porcelain -z
(cd "$tmp/wt with spaces" && capture worktree-list-from-linked.bin git worktree list --porcelain -z)

git clone -q --bare "$tmp/repo" "$tmp/bare.git"
git -C "$tmp/bare.git" worktree add -q "$tmp/wt-from-bare" main
(cd "$tmp/bare.git" && capture worktree-list-bare.bin git worktree list --porcelain -z)

# --- status -------------------------------------------------------------------
at "2026-01-16T10:00:00+00:00"
commit tracked.txt "one" "Add tracked file"
commit "to rename.txt" "rename me" "Add file to rename"
commit doomed.txt "bye" "Add file to delete"
echo "changed" > tracked.txt
git mv "to rename.txt" "renamed åäö.txt"
git rm -q doomed.txt
echo "new" > staged.txt && git add staged.txt
echo "untracked" > "untracked file.txt"
echo "untracked" > "otäckt.txt"
capture status-upstream-ahead.bin git status --porcelain=v2 --branch -z
git stash push -q --include-untracked -m "autostash"

(cd "$tmp/wt with spaces" && echo "x" > new.txt && capture status-no-upstream.bin git status --porcelain=v2 --branch -z)
(cd "$tmp/wt-detached" && capture status-detached-clean.bin git status --porcelain=v2 --branch -z)

git init -q -b main "$tmp/empty"
(cd "$tmp/empty" && echo "x" > first.txt && capture status-initial.bin git status --porcelain=v2 --branch -z)

git init -q -b main "$tmp/conflict"
(
  cd "$tmp/conflict"
  commit file.txt "base" "Base"
  git switch -q -c other && commit file.txt "theirs" "Other side"
  git switch -q main && commit file.txt "ours" "Main side"
  git merge -q other > /dev/null 2>&1 || true
  capture status-conflict.bin git status --porcelain=v2 --branch -z
)

# --- refs ---------------------------------------------------------------------
git push -q origin main
git switch -q -c pushed-then-gone
commit gone.txt "gone" "Work on gone branch"
git push -q -u origin pushed-then-gone
git push -q origin --delete pushed-then-gone
git fetch -q --prune
git switch -q -c ahead-behind main
commit ahead.txt "ahead" "Ahead commit"
git push -q -u origin ahead-behind
commit ahead2.txt "ahead2" "Second ahead commit"
git switch -q -c diverged main
git branch -q -u origin/ahead-behind
commit diverged.txt "diverged" "Diverged commit"
git switch -q main
git tag v1.0
git tag -a archive/claude/old-work -m "Backup before cleanup"
for seq in 1 2 3; do
  at "2026-01-1${seq}T09:00:00+00:00"
  git update-ref "refs/copilot/checkpoints/7f3c2a9e-session/$seq/cp-$seq" HEAD
done
echo "more" >> tracked.txt
git stash push -q -m "Fix för åäö" -- tracked.txt
capture refs.bin git for-each-ref "--format=$refs_format"
capture refs-empty.bin git -C "$tmp/empty" for-each-ref "--format=$refs_format"

# --- stashes -----------------------------------------------------------------
echo "wip" >> readme.md
git stash push -q
capture stash-list.bin git stash list -z "--format=$stash_format"
capture stash-list-empty.bin git -C "$tmp/empty" stash list -z "--format=$stash_format"

# --- unreachable --------------------------------------------------------------
commit orphan.txt "orphan" "Commit that gets reset away"
git reset -q --hard HEAD~1
git stash drop -q
capture fsck-unreachable.txt git fsck --unreachable --no-reflogs --no-progress
git init -q -b main "$tmp/clean"
(cd "$tmp/clean" && commit a.txt "a" "Only commit" && capture fsck-clean.txt git fsck --unreachable --no-reflogs --no-progress)

echo "Samples written to $out"
