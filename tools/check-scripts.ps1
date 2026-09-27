<#
.SYNOPSIS
    Parse-sweep + ASCII-byte scan for every .ps1/.psm1/.psd1 in the repo.

.DESCRIPTION
    1) Parses every script with the PowerShell 5.1 parser
       ([System.Management.Automation.Language.Parser]::ParseFile) and fails on any parse
       error, so a pwsh-only construct (?:, ??, ?.) or a syntax mistake is caught even on a
       machine that also has PowerShell 7 installed.
    2) Scans the raw bytes of every script for anything outside 7-bit ASCII (0x00-0x7F). A
       BOM-less .ps1 with a non-ASCII character (an em dash, a smart quote, ...) is read as
       cp1252 by PowerShell 5.1 and can silently fail to parse or corrupt string literals.

    Excludes build output (bin/, obj/, out/) and .git/. Exit 0 on a clean sweep, exit 1 and a
    printed list of every problem otherwise. PowerShell 5.1 compatible. ASCII only.
#>

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

$excludedSegments = @('\bin\', '\obj\', '\out\', '\.git\')

$scripts = Get-ChildItem -Path $repoRoot -Recurse -Include '*.ps1', '*.psm1', '*.psd1' -File |
    Where-Object {
        $path = $_.FullName
        $excluded = $false
        foreach ($segment in $excludedSegments) {
            if ($path -like "*$segment*") {
                $excluded = $true
                break
            }
        }
        -not $excluded
    }

$problems = New-Object System.Collections.Generic.List[string]

foreach ($script in $scripts) {
    $relativePath = $script.FullName.Substring($repoRoot.Length).TrimStart('\')

    # --- 1) PS 5.1 parse check ---
    $tokens = $null
    $parseErrors = $null
    try {
        [void][System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$parseErrors)
    }
    catch {
        $problems.Add("PARSE ERROR: $relativePath : $($_.Exception.Message)")
        continue
    }

    if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) {
        foreach ($parseError in $parseErrors) {
            $problems.Add("PARSE ERROR: $relativePath : $($parseError.Message) (line $($parseError.Extent.StartLineNumber))")
        }
    }

    # --- 2) ASCII byte scan ---
    $bytes = [System.IO.File]::ReadAllBytes($script.FullName)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -gt 0x7F) {
            $problems.Add("NON-ASCII BYTE: $relativePath : byte 0x$($bytes[$i].ToString('X2')) at offset $i")
            break
        }
    }
}

Write-Host ("check-scripts.ps1: scanned {0} script(s)" -f $scripts.Count)

if ($problems.Count -gt 0) {
    Write-Host ("check-scripts.ps1: {0} problem(s) found:" -f $problems.Count)
    foreach ($problem in $problems) {
        Write-Host $problem
    }

    exit 1
}

Write-Host 'check-scripts.ps1: clean.'
exit 0
