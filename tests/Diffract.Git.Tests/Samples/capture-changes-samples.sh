#!/usr/bin/env bash
# Regenerates the Changes view's status samples (changes-*.bin) in this folder from real git output.
# Separate from capture-samples.sh, so the other samples stay as they are.
#
# Every repo is created inside a new temp folder that is deleted at the end.
# Nothing outside that folder is written by git. Run from Git Bash, or any bash:
#   tests/Diffract.Git.Tests/Samples/capture-changes-samples.sh
#
# The arguments must match StatusParser.ChangesArguments and FilePatch.Arguments. The tests check that they do.
# Status prints paths relative to the worktree, so no temp path ends up in a sample.
set -euo pipefail

out="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
case "$tmp" in */tmp.*) ;; *) echo "mktemp gave an unexpected path: $tmp" >&2; exit 1 ;; esac
trap 'rm -rf -- "$tmp"' EXIT
if command -v cygpath > /dev/null; then tmp="$(cygpath -m "$tmp")"; fi

# Isolate git from the developer's own config and make commits deterministic.
: > "$tmp/gitconfig"
export GIT_CONFIG_GLOBAL="$tmp/gitconfig" GIT_CONFIG_NOSYSTEM=1
export GIT_AUTHOR_NAME="Sample Author" GIT_AUTHOR_EMAIL="author@example.com"
export GIT_COMMITTER_NAME="Sample Author" GIT_COMMITTER_EMAIL="author@example.com"
export GIT_AUTHOR_DATE="2026-01-15T10:00:00+00:00" GIT_COMMITTER_DATE="2026-01-15T10:00:00+00:00"
export GIT_OPTIONAL_LOCKS=0 GIT_TERMINAL_PROMPT=0
git config --global core.autocrlf false
git config --global protocol.file.allow always

commit() { printf '%s\n' "$2" > "$1"; git add -- "$1"; git commit -q -m "$3"; }
status() { git status --porcelain=v2 --branch -z --untracked-files=all; }

# --- a busy worktree --------------------------------------------------------
# A branch that is 1 ahead of and 2 behind its upstream, and every kind of change but a conflict.
git init -q -b library "$tmp/library"
(cd "$tmp/library" && commit lib.txt "v1" "Library")
git init -q --bare -b main "$tmp/origin.git"
git init -q -b main "$tmp/repo"
cd "$tmp/repo"
commit base.txt "base" "Base"
git remote add origin "$tmp/origin.git"
git push -q -u origin main
git clone -q "$tmp/origin.git" "$tmp/other"
(cd "$tmp/other" && commit other1.txt "1" "Other 1" && commit other2.txt "2" "Other 2" && git push -q)
git fetch -q
# A relative URL, so .gitmodules, and with it every commit id, is the same on every run.
git submodule -q add ../library "vendor/library"
for f in modified.txt staged.txt both.txt deleted.txt removed.txt "to rename.txt" "rename then edit.txt"; do
  # Each file its own content, so git pairs each rename with the right file.
  printf '%s, line 1\n%s, line 2\n' "$f" "$f" > "$f"
done
git add -A && git commit -q -m "Files to change"
(cd vendor/library && commit lib.txt "v2" "Newer library")
echo "changed" >> modified.txt
echo "changed" >> staged.txt && git add staged.txt
echo "staged" >> both.txt && git add both.txt && echo "unstaged" >> both.txt
git rm -q deleted.txt
rm removed.txt
git mv "to rename.txt" "renamed åäö.txt"
git mv "rename then edit.txt" "renamed then edited.txt" && echo "edited" >> "renamed then edited.txt"
echo "new" > added.txt && git add added.txt
echo "intent" > "intent to add.txt" && git add --intent-to-add "intent to add.txt"
mkdir -p "new folder/räksmörgås" && echo "deep" > "new folder/räksmörgås/deep file.txt"
echo "untracked" > "untracked file.txt"
git worktree add -q -b claude/task ".claude/worktrees/task"
status > "$out/changes-status.bin"

# --- a merge that stopped on conflicts ---------------------------------------
git init -q -b main "$tmp/conflict"
cd "$tmp/conflict"
commit both.txt "base" "Base"
git switch -q -c other
commit both.txt "theirs" "Other side"
commit added.txt "theirs" "Other adds"
git switch -q main
commit both.txt "ours" "Main side"
commit added.txt "ours" "Main adds"
commit clean.txt "clean" "Unrelated"
git merge -q other > /dev/null 2>&1 || true
echo "staged during the merge" > clean.txt && git add clean.txt
status > "$out/changes-status-conflict.bin"

# --- hunks: diffs without context, as the Changes view stages them ------------
# The diff samples are byte for byte what git wrote, CRs included.
diff_args='-U0 --no-color --no-ext-diff --no-textconv --src-prefix=a/ --dst-prefix=b/'
hunks() { git diff $diff_args "$@"; }
git init -q -b main "$tmp/hunks"
cd "$tmp/hunks"
printf '1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n' > "near åäö.txt"
printf 'one\r\ntwo\r\nthree\r\n' > crlf.txt
printf 'first\nlast' > no-newline.txt
printf 'keep\nrename me\nplease\n' > old.txt
printf 'bin\0ary' > image.bin
git add -A && git commit -q -m "Files"
printf '1\nTWO\n3\n4\n5\nSIX\n7\n8\n9\n10\n' > "near åäö.txt"
printf 'one\r\nTWO\r\nthree\r\n' > crlf.txt
printf 'first\nlast\nafter' > no-newline.txt
printf 'new\nfile\n' > new.txt && git add --intent-to-add new.txt
printf 'b\0in' > image.bin
hunks -- ":(literal)near åäö.txt" > "$out/diff-two-hunks.bin"
hunks -- ":(literal)crlf.txt" > "$out/diff-crlf.bin"
hunks -- ":(literal)no-newline.txt" > "$out/diff-no-newline.bin"
hunks -- ":(literal)new.txt" > "$out/diff-new-file.bin"
hunks -- ":(literal)image.bin" > "$out/diff-binary.bin"
git mv old.txt "renamed.txt" && printf 'keep\nrenamed\nplease\n' > renamed.txt && git add renamed.txt
hunks --cached -M -- ":(literal)renamed.txt" ":(literal)old.txt" > "$out/diff-staged-rename.bin"

echo "Samples written to $out"
