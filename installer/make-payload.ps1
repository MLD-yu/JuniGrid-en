# Compress publish\sc (the main app's self-contained output) into the installer-embedded payload.lz
# (JGP1 container + single-stream LZMA solid compression, 25-30% smaller than the old per-file Deflate payload.zip)
$ErrorActionPreference = 'Stop'
$installerDir = $PSScriptRoot
$projectDir   = Join-Path $installerDir 'JuniGridInstaller'
$repo         = Split-Path -Parent $installerDir
$sc           = Join-Path $repo 'publish\sc'

if (-not (Test-Path (Join-Path $sc 'JuniGrid.exe'))) {
    Write-Host "publish\sc not found; publishing the main app first (self-contained)…"
    dotnet publish (Join-Path $repo 'JuniGrid') -c Release -r win-x64 --self-contained true -p:DebugType=none -o $sc
    if ($LASTEXITCODE -ne 0) { throw "Main app publish failed" }
}

$payload = Join-Path $projectDir 'payload.lz'
Remove-Item $payload -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $projectDir 'payload.zip') -Force -ErrorAction SilentlyContinue

# PayloadTool: after compressing, automatically extracts everything and compares SHA-256; throws immediately on any hash mismatch
dotnet run --project (Join-Path $installerDir 'PayloadTool') -c Release -- c $sc $payload
if ($LASTEXITCODE -ne 0) { throw "payload compression/verification failed" }

Write-Host ("payload.lz: {0:N1} MB" -f ((Get-Item $payload).Length / 1MB))
