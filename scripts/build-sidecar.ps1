# Publishes the sidecar as a Native AOT binary and copies it to src-tauri/binaries, where Tauri bundles it from.
# Tauri expects the file name to end with the Rust target triple.
# Run before `tauri dev` or `tauri build`, and again after changing sidecar code.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\sidecar'

dotnet publish (Join-Path $root 'sidecar\Diffract.Sidecar\Diffract.Sidecar.csproj') -c Release -r win-x64 -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$binaries = Join-Path $root 'src-tauri\binaries'
New-Item -ItemType Directory -Force $binaries | Out-Null
Copy-Item (Join-Path $out 'diffract-sidecar.exe') (Join-Path $binaries 'diffract-sidecar-x86_64-pc-windows-msvc.exe') -Force
Write-Host "Sidecar copied to $binaries"
