#Requires -Version 5.1
<#
.SYNOPSIS
  Fetches the pinned PresentMon v2.6.0-x64 release asset (docs/SPEC.md Component 9 /
  step7-fps) into third_party/presentmon/ and verifies its SHA-256 hash and size before
  leaving it in place. Never committed to git (third_party/ is gitignored); the app copies
  it into its own output folder at build time if present (HardwareLive.App.csproj).

.DESCRIPTION
  Idempotent: if the exe already exists at the expected path with the expected hash, this
  script does nothing but report success. On a hash or size mismatch (a corrupt download, or
  someone bumping the pinned version without updating this script) the bad file is deleted
  and the script exits 1 rather than leaving an unverified binary in place.

  ASCII-only, PowerShell 5.1-safe (this repo's convention for every .ps1): no smart quotes,
  no PS7-only operators.
#>

$ErrorActionPreference = 'Stop'

$PresentMonVersion = 'v2.6.0'
$AssetFileName = 'PresentMon-2.6.0-x64.exe'
$AssetUrl = "https://github.com/GameTechDev/PresentMon/releases/download/$PresentMonVersion/$AssetFileName"
$LicenseUrl = "https://raw.githubusercontent.com/GameTechDev/PresentMon/$PresentMonVersion/LICENSE.txt"
$ExpectedSha256 = 'b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af'
$ExpectedSizeBytes = 980320

$RepoRoot = Split-Path -Parent $PSScriptRoot
$TargetDir = Join-Path $RepoRoot 'third_party\presentmon'
$TargetExe = Join-Path $TargetDir $AssetFileName
$TargetLicense = Join-Path $TargetDir 'LICENSE.txt'

function Get-FileSha256([string]$Path) {
    # Not Get-FileHash: some hardened/sandboxed PowerShell 5.1 hosts ship
    # Microsoft.PowerShell.Utility without that cmdlet even present. SHA256 via .NET
    # directly has no such dependency.
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

    $sb = New-Object System.Text.StringBuilder
    foreach ($b in $bytes) {
        [void]$sb.Append($b.ToString('x2'))
    }

    return $sb.ToString()
}

function Test-ExistingAsset {
    if (-not (Test-Path -LiteralPath $TargetExe)) {
        return $false
    }

    $actualSize = (Get-Item -LiteralPath $TargetExe).Length
    if ($actualSize -ne $ExpectedSizeBytes) {
        return $false
    }

    $actualHash = Get-FileSha256 -Path $TargetExe
    return $actualHash -eq $ExpectedSha256
}

if (-not (Test-Path -LiteralPath $TargetDir)) {
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
}

if (Test-ExistingAsset) {
    Write-Host "PresentMon $PresentMonVersion already present and verified at $TargetExe"
}
else {
    if (Test-Path -LiteralPath $TargetExe) {
        Remove-Item -LiteralPath $TargetExe -Force
    }

    Write-Host "Downloading PresentMon $PresentMonVersion from $AssetUrl"
    try {
        Invoke-WebRequest -Uri $AssetUrl -OutFile $TargetExe -UseBasicParsing
    }
    catch {
        Write-Error "Download failed: $_"
        exit 1
    }

    $actualSize = (Get-Item -LiteralPath $TargetExe).Length
    $actualHash = Get-FileSha256 -Path $TargetExe

    if ($actualSize -ne $ExpectedSizeBytes -or $actualHash -ne $ExpectedSha256) {
        Write-Error ("PresentMon download failed verification. " +
            "Expected size $ExpectedSizeBytes bytes / sha256 $ExpectedSha256, " +
            "got size $actualSize bytes / sha256 $actualHash. Deleting the bad file.")
        Remove-Item -LiteralPath $TargetExe -Force -ErrorAction SilentlyContinue
        exit 1
    }

    Write-Host "PresentMon $PresentMonVersion verified: size $actualSize bytes, sha256 $actualHash"
}

if (-not (Test-Path -LiteralPath $TargetLicense) -or (Get-Item -LiteralPath $TargetLicense).Length -eq 0) {
    Write-Host "Fetching PresentMon license from $LicenseUrl"
    try {
        Invoke-WebRequest -Uri $LicenseUrl -OutFile $TargetLicense -UseBasicParsing
    }
    catch {
        Write-Error "License download failed: $_"
        exit 1
    }

    if ((Get-Item -LiteralPath $TargetLicense).Length -eq 0) {
        Write-Error "Downloaded license file is empty."
        Remove-Item -LiteralPath $TargetLicense -Force -ErrorAction SilentlyContinue
        exit 1
    }
}

Write-Host "PresentMon fetch complete."
exit 0
