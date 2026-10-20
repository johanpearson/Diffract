# Builds the milestone 1 fixture repos so they can be inspected by hand.
# The output folder must be empty (or not exist) and must not be inside a git repo.
#
#   ./tests/fixtures/make-repos.ps1 C:\temp\diffract-fixtures
#   ./tests/fixtures/make-repos.ps1 C:\temp\diffract-fixtures -Scenario StaleAgentWorktree
param(
    [Parameter(Mandatory)][string]$OutputDir,
    [string]$Scenario
)

$spike = "$PSScriptRoot/../../tools/Diffract.Spike"
$spikeArgs = @('fixtures', $OutputDir)
if ($Scenario) { $spikeArgs += @('--scenario', $Scenario) }

dotnet run --project $spike -- @spikeArgs
exit $LASTEXITCODE
