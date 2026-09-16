# One-shot build of the Riot-style in-house installer: dist-tmp\JuniGrid-en-v<version>-setup.exe
# Usage: powershell -File build-installer.ps1 [-SkipAppPublish]
#   -SkipAppPublish  Reuse the existing publish\sc without republishing the main app (for day-to-day installer builds)
$param = $args
$ErrorActionPreference = 'Stop'
$installerDir = $PSScriptRoot
$repo = Split-Path -Parent $installerDir

# 1) Single source of truth for the version: JuniGrid\Services\AppInfo.cs
$version = [regex]::Match((Get-Content (Join-Path $repo 'JuniGrid\Services\AppInfo.cs') -Raw),
    'Version\s*=\s*"([^"]+)"').Groups[1].Value
if (-not $version) { throw "Could not read the version from AppInfo.cs" }
Write-Host "=== JuniGrid installer v$version ==="

$skipPublish = $param -contains '-SkipAppPublish'

# 2) Self-contained publish of the main app
$sc = Join-Path $repo 'publish\sc'
if (-not $skipPublish -or -not (Test-Path (Join-Path $sc 'JuniGrid.exe'))) {
    # -p:Version=$version keeps FileVersion/ProductVersion in lockstep with AppInfo.Version
    # (Nexus AUP: Application-Version HTTP header + the released build must always match).
    dotnet publish (Join-Path $repo 'JuniGrid') -c Release -r win-x64 --self-contained true -p:DebugType=none -p:Version=$version -o $sc
    if ($LASTEXITCODE -ne 0) { throw "Main app publish failed" }
}

# 3) Build payload.lz (LZMA solid container, includes full SHA-256 verification)
& (Join-Path $installerDir 'make-payload.ps1')

# 4) Single-file publish of the installer (embeds payload.lz)
# Note: must include IncludeNativeLibrariesForSelfExtract=true — with a compressed bundle the WPF native
#       libraries need self-extraction to load, otherwise startup fails with DllNotFoundException.
# Note: compressed bundles conflict with OneDrive sync folders — do not run the setup package directly
#       from a OneDrive folder; download it locally (e.g. Downloads) and run it from there.
$out = Join-Path $installerDir 'publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $installerDir 'JuniGridInstaller') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:Version=$version -o $out
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed" }

# 5) Copy to dist-tmp (naming matches the old Inno output, so SelfUpdateService needs no changes)
$dist = Join-Path $repo 'dist-tmp'
New-Item -ItemType Directory -Force $dist | Out-Null
$dest = Join-Path $dist ("JuniGrid-en-v{0}-setup.exe" -f $version)
Copy-Item (Join-Path $out 'JuniGridSetup.exe') $dest -Force
Write-Host ("Output: {0}  ({1:N1} MB)" -f $dest, ((Get-Item $dest).Length / 1MB))
