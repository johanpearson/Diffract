#!/usr/bin/env bash
# Regenerates the history parser samples (log-*.bin and details-*.bin) in this folder from real git output.
# Separate from capture-samples.sh, so the other samples stay as they are.
#
# Every repo is created inside a new temp folder that is deleted at the end.
# Nothing outside that folder is written by git. Run from Git Bash:
#   tests/Diffract.Git.Tests/Samples/capture-history-samples.sh
#
# The formats must match CommitLogParser.Format and CommitDetailsParser.Format. The tests check that they do.
set -euo pipefail

out="$(cd "$(dirname "$0")" && pwd)"
tmp_unix="$(mktemp -d)"
case "$tmp_unix" in */tmp.*) ;; *) echo "mktemp gave an unexpected path: $tmp_unix" >&2; exit 1 ;; esac
trap 'rm -rf -- "$tmp_unix"' EXIT
tmp="$(cygpath -m "$tmp_unix")"

# Isolate git from the developer's own config and make commits deterministic.
: > "$tmp/gitconfig"
export GIT_CONFIG_GLOBAL="$tmp/gitconfig" GIT_CONFIG_NOSYSTEM=1
export GIT_OPTIONAL_LOCKS=0 GIT_TERMINAL_PROMPT=0
git config --global core.autocrlf false

as() { export GIT_AUTHOR_NAME="$1" GIT_AUTHOR_EMAIL="$2" GIT_COMMITTER_NAME="$1" GIT_COMMITTER_EMAIL="$2"; }
at() { export GIT_AUTHOR_DATE="$1" GIT_COMMITTER_DATE="$1"; }
commit() { printf '%s\n' "$2" > "$1"; git add -- "$1"; git commit -q -m "$3"; }
as "Sample Author" "author@example.com"
at "2026-01-15T10:00:00+00:00"

log_format='%H%x00%P%x00%an%x00%ae%x00%at%x00%ct%x00%s'
details_format='%H%x00%P%x00%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI%x00%s%x00%b'
log() { git log --topo-order -z --no-show-signature "--format=$log_format" --branches --remotes --tags "$@" --; }
details() { git diff-tree -r -z --always --root -M --diff-merges=first-parent --raw --numstat "--format=$details_format" "$1"; }

git init -q -b main "$tmp/repo"
cd "$tmp/repo"

# A root commit, then a branch and merge, then an octopus of three branches.
commit readme.md "hello" "Initial commit"
root="$(git rev-parse HEAD)"
at "2026-01-16T10:00:00+00:00"
git switch -q -c feature
commit feature.txt "feature" "Add feature"
git switch -q main
commit main.txt "main" "Work on main"
git merge -q --no-ff --no-edit -m "Merge branch 'feature'" feature
merge="$(git rev-parse HEAD)"

at "2026-01-17T10:00:00+02:00"
for b in one two three; do
  git switch -q -c "octo-$b" main
  commit "$b.txt" "$b" "Octopus side $b"
done
git switch -q main
git merge -q --no-edit -m "Octopus merge" octo-one octo-two octo-three
octopus="$(git rev-parse HEAD)"

# Non-ASCII author, subject and paths, an empty subject, and a commit stored in Latin-1.
at "2026-01-18T10:00:00-05:00"
as "Åsa Öberg" "åsa@exempel.se"
commit "smörgås.txt" "räksmörgås" "Lägg till räksmörgås 🦐"
printf 'x\n' >> readme.md && git add readme.md && git commit -q --allow-empty-message -m ""
empty_subject="$(git rev-parse HEAD)"
# git re-encodes the whole commit, names included, so a UTF-8 name in a Latin-1 commit would come out garbled.
as "Sample Author" "author@example.com"
printf 'Latin-1 \xe5\xe4\xf6\n' > latin1-message.txt
printf 'l\n' > latin1.txt && git add latin1.txt
git -c i18n.commitEncoding=ISO-8859-1 commit -q -F latin1-message.txt
rm latin1-message.txt

# Every kind of file change in one commit, with a body.
at "2026-01-19T10:00:00+00:00"
printf 'one\ntwo\nthree\nfour\nfive\n' > "to rename.txt"
printf 'gone\n' > doomed.txt
printf 'run\n' > script.sh
git add . && git commit -q -m "Prepare files"
git mv "to rename.txt" "renamed åäö.txt"
git rm -q doomed.txt
printf 'six\n' >> "renamed åäö.txt"
printf 'new file with spaces\n' > "new file.txt"
printf '\x00\x01\x02binary' > image.bin
git update-index --chmod=+x script.sh
printf 'changed\n' > main.txt
git add . && git commit -q -m "Change every kind of file" -m "The body has two paragraphs.

Second paragraph, with åäö."
files="$(git rev-parse HEAD)"

# A submodule entry, an empty commit, and a second root commit on its own branch.
git update-index --add --cacheinfo "160000,$root,vendor/lib"
git commit -q -m "Add a submodule"
submodule="$(git rev-parse HEAD)"
git commit -q --allow-empty -m "Nothing changed"
empty="$(git rev-parse HEAD)"
git switch -q --orphan pages
commit index.html "<p>pages</p>" "Second root"
git switch -q main

# A tag, a tag on a tree, and a detached worktree HEAD that no branch reaches.
git tag v1.0 "$merge"
git tag -a tree-tag -m "Tag on a tree" "HEAD^{tree}"
git worktree add -q --detach "$tmp/detached" main
(cd "$tmp/detached" && commit detached.txt "d" "Only on a detached HEAD")
detached="$(git -C "$tmp/detached" rev-parse HEAD)"

log "$detached" > "$out/log-history.bin"
details "$root" > "$out/details-root.bin"
details "$merge" > "$out/details-merge.bin"
details "$octopus" > "$out/details-octopus.bin"
details "$empty_subject" > "$out/details-empty-subject.bin"
details "$files" > "$out/details-files.bin"
details "$submodule" > "$out/details-submodule.bin"
details "$empty" > "$out/details-empty.bin"

git init -q -b main "$tmp/empty"
(cd "$tmp/empty" && log > "$out/log-empty.bin")

echo "History samples written to $out"
