<#
.SYNOPSIS
    Builds the release zip: self-contained win-x64 publish of the app and sampler into
    separate folders, plus licenses, install.ps1, uninstall.ps1 and README-INSTALL.txt
    (docs/SPEC.md Component 8 "Publish layout").

.PARAMETER Version
    The release version, e.g. 0.1.0-alpha.1. Used for the output folder/zip name.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\package.ps1 -Version 0.1.0-alpha.1
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName 'System.IO.Compression.FileSystem'

$ToolsDir = Split-Path -Parent $PSCommandPath
$RepoRoot = Split-Path -Parent $ToolsDir
$OutRoot = Join-Path $RepoRoot 'out'
$PackageName = "HardwareLive-$Version"
$PackageDir = Join-Path $OutRoot $PackageName
$ZipPath = Join-Path $OutRoot "$PackageName-win-x64.zip"

function Write-PackageLog {
    param([string]$Message)
    Write-Host "package.ps1: $Message"
}

# Not Get-FileHash: on a machine where PSModulePath resolves Microsoft.PowerShell.Utility to a
# pwsh-7-only build ahead of the native Windows PowerShell 5.1 one (a real observed PATH
# contamination case), the whole module -- and therefore Get-FileHash -- silently fails to
# auto-load under powershell.exe. .NET's own SHA256 has no such dependency.
function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            $bytes = $sha256.ComputeHash($stream)
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $sha256.Dispose()
    }

    return -join ($bytes | ForEach-Object { $_.ToString('x2') })
}

if (Test-Path -LiteralPath $PackageDir) {
    Remove-Item -LiteralPath $PackageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $PackageDir -Force | Out-Null

if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}

# ---------------------------------------------------------------------------
# FPS (PresentMon): tools/fetch-presentmon.ps1 downloads + hash-verifies the pinned asset
# into third_party/presentmon/ (it takes no parameters). Let a hash-mismatch failure inside
# it propagate (fail the package rather than ship an unverified binary); the verified files
# are copied into app\presentmon after publish. If the script is absent, package without FPS.
# ---------------------------------------------------------------------------

$FetchPresentMonScript = Join-Path $ToolsDir 'fetch-presentmon.ps1'
$AppPresentMonDir = Join-Path $PackageDir 'app\presentmon'
$ThirdPartyPresentMonDir = Join-Path $RepoRoot 'third_party\presentmon'
$PresentMonExeName = 'PresentMon-2.6.0-x64.exe'
$includeFps = $false

if (Test-Path -LiteralPath $FetchPresentMonScript) {
    Write-PackageLog "running fetch-presentmon.ps1..."
    # $LASTEXITCODE is only ever set by a *native* command; under StrictMode -Version 2 it is
    # genuinely undefined here if no native exe has run yet in this session, so guard the read
    # rather than dereference it directly.
    $fetchPresentMonExitCode = 0
    try {
        & $FetchPresentMonScript
        if (Test-Path variable:LASTEXITCODE) {
            $fetchPresentMonExitCode = $LASTEXITCODE
        }
    }
    catch {
        throw "fetch-presentmon.ps1 threw: $($_.Exception.Message)"
    }
    if ($fetchPresentMonExitCode -and $fetchPresentMonExitCode -ne 0) {
        throw "fetch-presentmon.ps1 failed (exit $fetchPresentMonExitCode); refusing to package an unverified FPS binary."
    }
    $includeFps = $true
    Write-PackageLog "FPS: included (fetch-presentmon.ps1 ran successfully)."
}
else {
    Write-PackageLog "FPS: not included (fetch-presentmon.ps1 not found)"
}

# ---------------------------------------------------------------------------
# Publish app + sampler, self-contained win-x64, separate folders.
# ---------------------------------------------------------------------------

$AppProject = Join-Path $RepoRoot 'src\HardwareLive.App\HardwareLive.App.csproj'
$SamplerProject = Join-Path $RepoRoot 'src\HardwareLive.Sampler\HardwareLive.Sampler.csproj'
$AppOutDir = Join-Path $PackageDir 'app'
$SamplerOutDir = Join-Path $PackageDir 'sampler'

Write-PackageLog "publishing app -> $AppOutDir"
dotnet publish $AppProject -c Release -r win-x64 --self-contained true -o $AppOutDir `
    -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for HardwareLive.App (exit $LASTEXITCODE)"
}

Write-PackageLog "publishing sampler -> $SamplerOutDir"
dotnet publish $SamplerProject -c Release -r win-x64 --self-contained true -o $SamplerOutDir `
    -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for HardwareLive.Sampler (exit $LASTEXITCODE)"
}

if (-not (Test-Path -LiteralPath (Join-Path $AppOutDir 'hardware-live.exe'))) {
    throw "Expected app\hardware-live.exe was not produced by publish."
}
if (-not (Test-Path -LiteralPath (Join-Path $SamplerOutDir 'hl-sampler.exe'))) {
    throw "Expected sampler\hl-sampler.exe was not produced by publish."
}

if ($includeFps) {
    $srcExe = Join-Path $ThirdPartyPresentMonDir $PresentMonExeName
    if (-not (Test-Path -LiteralPath $srcExe)) {
        throw "fetch-presentmon.ps1 reported success but $srcExe is missing."
    }
    New-Item -ItemType Directory -Path $AppPresentMonDir -Force | Out-Null
    Copy-Item -LiteralPath $srcExe -Destination (Join-Path $AppPresentMonDir $PresentMonExeName) -Force
    $srcLicense = Join-Path $ThirdPartyPresentMonDir 'LICENSE.txt'
    if (Test-Path -LiteralPath $srcLicense) {
        Copy-Item -LiteralPath $srcLicense -Destination (Join-Path $AppPresentMonDir 'LICENSE.txt') -Force
    }
}

# ---------------------------------------------------------------------------
# Licenses
# ---------------------------------------------------------------------------

$LicensesDir = Join-Path $PackageDir 'licenses'
New-Item -ItemType Directory -Path $LicensesDir -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $RepoRoot 'LICENSE') -Destination (Join-Path $LicensesDir 'LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $LicensesDir 'THIRD-PARTY-NOTICES.md') -Force

$mplText = Get-Content -LiteralPath (Join-Path $ToolsDir 'licenses\MPL-2.0.txt') -Raw

$LhmLicenseNotice = @'
LibreHardwareMonitorLib 0.9.6 -- Mozilla Public License 2.0
Source: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
Shipped unmodified as a NuGet library reference (LibreHardwareMonitorLib.dll); no source
changes were made. Full MPL-2.0 text follows.

'@ + $mplText

[System.IO.File]::WriteAllText((Join-Path $LicensesDir 'LibreHardwareMonitorLib-MPL-2.0.txt'), $LhmLicenseNotice, (New-Object System.Text.UTF8Encoding($false)))

if ($includeFps) {
    $presentMonLicense = Join-Path $AppPresentMonDir 'LICENSE.txt'
    if (Test-Path -LiteralPath $presentMonLicense) {
        Copy-Item -LiteralPath $presentMonLicense -Destination (Join-Path $LicensesDir 'PresentMon-LICENSE.txt') -Force
    }
    else {
        Write-PackageLog "WARN: FPS was included but fetch-presentmon.ps1 didn't produce a LICENSE file at $presentMonLicense"
    }
}

# ---------------------------------------------------------------------------
# Installer scripts + module + docs
# ---------------------------------------------------------------------------

Copy-Item -LiteralPath (Join-Path $RepoRoot 'install.ps1') -Destination (Join-Path $PackageDir 'install.ps1') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'uninstall.ps1') -Destination (Join-Path $PackageDir 'uninstall.ps1') -Force
# Shipped at the package root (not under tools\): install.ps1/uninstall.ps1 look for it next
# to themselves first, falling back to tools\ only for an in-repo dev run.
Copy-Item -LiteralPath (Join-Path $ToolsDir 'HardwareLive.InstallLib.psm1') -Destination (Join-Path $PackageDir 'HardwareLive.InstallLib.psm1') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'README-INSTALL.txt') -Destination (Join-Path $PackageDir 'README-INSTALL.txt') -Force

# ---------------------------------------------------------------------------
# SHA256SUMS.txt (written before zipping, so it ends up inside the zip)
# ---------------------------------------------------------------------------

$sumsPath = Join-Path $PackageDir 'SHA256SUMS.txt'
$sumLines = New-Object System.Collections.Generic.List[string]
$files = Get-ChildItem -LiteralPath $PackageDir -Recurse -File | Sort-Object FullName
foreach ($file in $files) {
    $relativePath = $file.FullName.Substring($PackageDir.Length + 1).Replace('\', '/')
    $hash = Get-Sha256Hex -Path $file.FullName
    $sumLines.Add("$hash  $relativePath")
}
[System.IO.File]::WriteAllText($sumsPath, ($sumLines -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))

# ---------------------------------------------------------------------------
# Zip (System.IO.Compression, not Compress-Archive: PS 5.1's Compress-Archive writes
# backslash-separated entry names, which most non-Windows unzip tools mis-handle).
# ---------------------------------------------------------------------------

Write-PackageLog "zipping -> $ZipPath"
[System.IO.Compression.ZipFile]::CreateFromDirectory($PackageDir, $ZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$zipHash = Get-Sha256Hex -Path $ZipPath
Write-PackageLog "done."
Write-Host ''
Write-Host "SHA256 ($ZipPath):"
Write-Host $zipHash
Write-Host ''
Write-Host 'Package tree:'
Get-ChildItem -LiteralPath $PackageDir -Recurse |
    ForEach-Object { $_.FullName.Substring($PackageDir.Length + 1).Replace('\', '/') } |
    Sort-Object |
    ForEach-Object { Write-Host "  $_" }
