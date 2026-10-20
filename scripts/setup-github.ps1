# Applies merge settings and the "Protect main" ruleset to GitHub.
# Needs the GitHub CLI (gh), logged in as the repo owner.
# Safe to run again: it updates the ruleset if it already exists.
# To switch protection off again, run scripts/github-unprotect.ps1.

$ErrorActionPreference = 'Stop'
$repo = 'johanpearson/Diffract'
$rulesetFile = Join-Path $PSScriptRoot '..\.github\rulesets\protect-main.json'
$rulesetName = (Get-Content $rulesetFile -Raw | ConvertFrom-Json).name

Write-Host "Merge settings: squash and rebase allowed, merge commits off, branches deleted after merge"
gh api "repos/$repo" --method PATCH `
  -F allow_squash_merge=true `
  -F allow_rebase_merge=true `
  -F allow_merge_commit=false `
  -F delete_branch_on_merge=true `
  -F allow_auto_merge=true `
  -f squash_merge_commit_title=PR_TITLE `
  -f squash_merge_commit_message=COMMIT_MESSAGES | Out-Null

$existing = gh api "repos/$repo/rulesets" --jq ".[] | select(.name == `"$rulesetName`") | .id"

if ($existing) {
  Write-Host "Updating ruleset '$rulesetName' ($existing)"
  gh api "repos/$repo/rulesets/$existing" --method PUT --input $rulesetFile | Out-Null
} else {
  Write-Host "Creating ruleset '$rulesetName'"
  gh api "repos/$repo/rulesets" --method POST --input $rulesetFile | Out-Null
}

Write-Host "Done. Check https://github.com/$repo/rules"
