<#
.SYNOPSIS
    Build the portable package from the Release output and strip sensitive data.

.DESCRIPTION
    Copies the binaries, loader runtimes, assets and the bundled .crx extensions
    into the portable folder, then removes anything that must not ship:
      - WebView2 password store:  Login Data*, Login Data For Account*
      - DPAPI wrapped master key:  Local State
      - Signed-in session cookies: Cookies
      - Restored session:          session.json
      - Diagnostics / symbols:     *.log, *.dmp, *.pdb, *.xml
    The Data folder is wiped so WebView2 starts with a clean profile on first run.

    Run from the dev repo after `dotnet build -c Release`.

.EXAMPLE
    pwsh scripts\pack-portable.ps1
#>

[CmdletBinding()]
param(
    [string]$Source,
    [string]$Target,
    [string]$CrxSource
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if (-not $Source)  { $Source  = Join-Path $repoRoot "bin\Release\net8.0-windows" }
if (-not $Target)  { $Target  = Join-Path (Split-Path $repoRoot -Parent) "DualPanelBrowser-Portable" }
if (-not $CrxSource) {
    $CrxSource = Join-Path (Split-Path $repoRoot -Parent) "WebView2DualPanel\bin\Release\net8.0-windows\Extensions"
}

$Source = [System.IO.Path]::GetFullPath($Source)
$Target = [System.IO.Path]::GetFullPath($Target)

$exe = Join-Path $Source "DualPanelBrowser.exe"
if (-not (Test-Path $exe)) {
    throw "Release output not found: $Source`nRun 'dotnet build -c Release' first."
}

Write-Host "Source: $Source"
Write-Host "Target: $Target"

# 1. Prepare the target folder. Keep the existing Extensions folder for now;
#    its .crx files are synced in step 3, unpacked subfolders are removed there.
if (Test-Path $Target) {
    Get-ChildItem $Target -Force | Where-Object { $_.Name -ne "Extensions" } |
        Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $Target | Out-Null
}

# 2. Copy binaries and resources. Never copy Data, *.pdb, *.xml, logs or session.
$copyItems = @(
    "DualPanelBrowser.exe",
    "DualPanelBrowser.dll",
    "DualPanelBrowser.deps.json",
    "DualPanelBrowser.runtimeconfig.json",
    "Microsoft.Web.WebView2.Core.dll",
    "Microsoft.Web.WebView2.WinForms.dll",
    "Microsoft.Web.WebView2.Wpf.dll",
    "runtimes",
    "assets",
    "extmanager.html"
)
foreach ($item in $copyItems) {
    $src = Join-Path $Source $item
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $Target $item) -Recurse -Force
    } else {
        Write-Warning "Missing in source, skipped: $item"
    }
}

# 3. Extensions: keep only .crx; drop unpacked dirs, load logs and disable marks.
$extTarget = Join-Path $Target "Extensions"
New-Item -ItemType Directory -Path $extTarget -Force | Out-Null
Get-ChildItem $extTarget -Force | Where-Object { $_.Extension -ne ".crx" } |
    Remove-Item -Recurse -Force
if (Test-Path $CrxSource) {
    Get-ChildItem $CrxSource -Filter "*.crx" | Copy-Item -Destination $extTarget -Force
} else {
    Write-Warning "CrxSource not found, kept existing .crx in target: $CrxSource"
}

# 4. Sensitive data strip (defensive: rules also cover files the copy step missed).
$sensitivePatterns = @(
    "Login Data*",
    "Local State",
    "Cookies",
    "session.json",
    "*.log",
    "*.dmp",
    "*.pdb",
    "*.xml"
)
foreach ($pattern in $sensitivePatterns) {
    Get-ChildItem $Target -Recurse -Force -Filter $pattern -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

# 5. Wipe the whole Data folder; WebView2 recreates an empty profile on first launch.
$dataDir = Join-Path $Target "Data"
if (Test-Path $dataDir) {
    Get-ChildItem $dataDir -Force | Remove-Item -Recurse -Force
}

# 6. Verification: no password store, master key, cookies, session, debug output,
#    symbols or SDK documentation should remain in the portable folder.
$leakPattern = "Login Data|Local State|Cookies|session.json|\.log$|\.dmp$|\.pdb$|\.xml$"
$leaks = Get-ChildItem $Target -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match $leakPattern }
if ($leaks) {
    Write-Warning "Sensitive or debug files still present:"
    $leaks | ForEach-Object { Write-Warning ("  " + $_.FullName) }
    exit 1
}

$crxCount = (Get-ChildItem $extTarget -Filter "*.crx" | Measure-Object).Count
$sizeMB = [Math]::Round(((Get-ChildItem $Target -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "OK - portable package: $Target"
Write-Host "  .crx extensions: $crxCount"
Write-Host "  size: $sizeMB MB"
