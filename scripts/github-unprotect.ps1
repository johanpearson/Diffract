# Escape hatch: switches the "Protect main" ruleset off without deleting it.
# Use it if a PR is stuck (for example a required check never reports) or you need to push to main directly.
# Run scripts/setup-github.ps1 afterwards to switch protection back on.

$ErrorActionPreference = 'Stop'
$repo = 'johanpearson/Diffract'
$rulesetName = 'Protect main'

$existing = gh api "repos/$repo/rulesets" --jq ".[] | select(.name == `"$rulesetName`") | .id"

if (-not $existing) {
  Write-Host "No ruleset named '$rulesetName'. Main is not protected."
  exit 0
}

gh api "repos/$repo/rulesets/$existing" --method PUT -f enforcement=disabled | Out-Null
Write-Host "Ruleset '$rulesetName' is disabled. Main accepts direct pushes until you run setup-github.ps1 again."
