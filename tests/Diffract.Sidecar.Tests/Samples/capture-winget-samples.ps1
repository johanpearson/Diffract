# Captures the winget output the sidecar tests read. Run from this folder in PowerShell.
# Every command here is `winget list`, which only reads what is installed. Nothing is installed or upgraded.
# The table's column widths depend on the rows, so a new capture may differ in spacing. That is fine: the parser
# finds the row by its id. winget-list-many.txt is a hand-picked part of a full `winget list --source winget`.

$list = @('list', '--exact', '--source', 'winget', '--disable-interactivity', '--id')

function Capture($id, $file) {
    $out = & winget @list $id
    "{0}: exit 0x{1:X8}" -f $file, $LASTEXITCODE
    # UTF-8 without a byte order mark, LF line endings, as the repo stores text.
    [IO.File]::WriteAllText((Join-Path $PWD $file), ($out -join "`n") + "`n")
}

Capture 'Git.Git' 'winget-list-git-update-available.txt'          # Git for Windows older than winget's newest
Capture 'GitExtensionsTeam.GitExtensions' 'winget-list-up-to-date.txt'  # any package with no newer version
Capture 'KDE.KDiff3' 'winget-list-unknown-version.txt'             # any package winget shows as "Unknown"
Capture 'No.Such.Package' 'winget-list-not-found.txt'              # exit 0x8A150014
