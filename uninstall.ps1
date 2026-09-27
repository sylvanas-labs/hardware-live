<#
.SYNOPSIS
    Uninstalls Hardware Live: stops + unregisters both scheduled tasks, removes Program Files
    and ProgramData, and offers to roll back the Performance Log Users membership it added
    (docs/SPEC.md Component 8/9). Idempotent -- safe to run twice.

.PARAMETER KeepUserData
    Keep %LOCALAPPDATA%\HardwareLive (layouts, config, notes). Default behavior if neither
    -KeepUserData nor -RemoveUserData is passed and the session isn't interactive.

.PARAMETER RemoveUserData
    Remove %LOCALAPPDATA%\HardwareLive.

.PARAMETER KeepLogs
    Keep %ProgramData%\HardwareLive\logs when removing %ProgramData%\HardwareLive.

.PARAMETER RemovePerfLogUsers
    Unattended: remove the Performance Log Users membership if install-state.json says this
    app added it, without asking interactively.

.PARAMETER KeepPerfLogUsers
    Unattended: leave the Performance Log Users membership alone, without asking interactively.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\uninstall.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -WhatIf
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$KeepUserData,
    [switch]$RemoveUserData,
    [switch]$KeepLogs,
    [switch]$RemovePerfLogUsers,
    [switch]$KeepPerfLogUsers,

    # --- internal, used only when this script relaunches itself elevated ---
    [string]$UserSid,
    [switch]$Elevated
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# See install.ps1 for why this is explicit rather than relying on module auto-load.
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop

$ScriptRoot = Split-Path -Parent $PSCommandPath
$InstallLibPath = Join-Path $ScriptRoot 'tools\HardwareLive.InstallLib.psm1'
if (-not (Test-Path -LiteralPath $InstallLibPath)) {
    $InstallLibPath = Join-Path $ScriptRoot 'HardwareLive.InstallLib.psm1'
}
Import-Module $InstallLibPath -Force

$ProgramFilesRoot = Join-Path $env:ProgramFiles 'HardwareLive'
$ProgramDataRoot = Join-Path $env:ProgramData 'HardwareLive'
$LogsDir = Join-Path $ProgramDataRoot 'logs'
$InstallStatePath = Join-Path $ProgramDataRoot 'install-state.json'
$TaskFolder = '\HardwareLive\'
$PerfLogUsersSid = 'S-1-5-32-559'

function Write-UninstallLog {
    param([string]$Message)
    Write-Host ("[{0}] {1}" -f (Get-Date -Format 's'), $Message)
}

function Invoke-GuardedAction {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    if ($PSCmdlet.ShouldProcess($Description)) {
        & $Action
    }
    else {
        Write-UninstallLog "WHATIF: would $Description"
    }
}

function Test-IsAdministrator {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---------------------------------------------------------------------------
# Self-elevate
# ---------------------------------------------------------------------------

$currentSid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
if (-not $UserSid) {
    $UserSid = $currentSid
}

if (-not $Elevated -and -not (Test-IsAdministrator)) {
    if ($WhatIfPreference) {
        Write-Host "WHATIF: would self-elevate via UAC (Start-Process -Verb RunAs) to uninstall for real."
    }
    else {
        Write-Host 'Hardware Live needs administrator rights to remove Program Files, ProgramData and the scheduled tasks. Requesting elevation now (UAC prompt)...'

        $argParts = New-Object System.Collections.Generic.List[string]
        $argParts.Add('-NoProfile')
        $argParts.Add('-ExecutionPolicy Bypass')
        $argParts.Add('-File "' + $PSCommandPath + '"')
        $argParts.Add('-Elevated')
        # Captured BEFORE elevation: if a standard user supplies admin credentials at the UAC
        # prompt, $env:USERNAME/the current token after elevation may be a different account
        # than the one whose %LOCALAPPDATA%\HardwareLive we should be asking about below.
        $argParts.Add('-UserSid "' + $currentSid + '"')
        if ($KeepUserData) { $argParts.Add('-KeepUserData') }
        if ($RemoveUserData) { $argParts.Add('-RemoveUserData') }
        if ($KeepLogs) { $argParts.Add('-KeepLogs') }
        if ($RemovePerfLogUsers) { $argParts.Add('-RemovePerfLogUsers') }
        if ($KeepPerfLogUsers) { $argParts.Add('-KeepPerfLogUsers') }

        $process = Start-Process -FilePath 'powershell.exe' -ArgumentList ($argParts -join ' ') -Verb RunAs -Wait -PassThru
        exit $process.ExitCode
    }
}

Write-UninstallLog "Hardware Live uninstall starting. WhatIf: $WhatIfPreference. Target user SID: $UserSid."

# ---------------------------------------------------------------------------
# Stop + unregister scheduled tasks
# ---------------------------------------------------------------------------

Invoke-GuardedAction -Description "stop and unregister the $TaskFolder Sampler / App tasks" -Action {
    $existingTasks = Get-ScheduledTask -TaskPath $TaskFolder -ErrorAction SilentlyContinue
    foreach ($task in $existingTasks) {
        try { Stop-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction SilentlyContinue } catch { }
        Unregister-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -Confirm:$false -ErrorAction SilentlyContinue
    }

    # Remove the now-empty \HardwareLive\ task folder. The Scheduler COM API is the only
    # supported way to remove an empty task folder (no ScheduledTasks cmdlet does it).
    try {
        $scheduler = New-Object -ComObject 'Schedule.Service'
        $scheduler.Connect()
        $rootFolder = $scheduler.GetFolder('\')
        $remainingTasks = Get-ScheduledTask -TaskPath $TaskFolder -ErrorAction SilentlyContinue
        if (-not $remainingTasks) {
            $rootFolder.DeleteFolder('HardwareLive', 0)
        }
    }
    catch {
        Write-UninstallLog "WARN: could not remove the (empty) $TaskFolder task folder: $($_.Exception.Message)"
    }
}

Invoke-GuardedAction -Description 'stop running hardware-live.exe / hl-sampler.exe' -Action {
    $processes = Get-Process -Name 'hardware-live', 'hl-sampler' -ErrorAction SilentlyContinue
    foreach ($process in $processes) {
        try { $process.Kill() } catch { }
    }
}

# ---------------------------------------------------------------------------
# Performance Log Users rollback (only if install-state.json says we added it)
# ---------------------------------------------------------------------------

$installState = $null
if (Test-Path -LiteralPath $InstallStatePath) {
    try {
        $installState = Get-Content -LiteralPath $InstallStatePath -Raw | ConvertFrom-Json
    }
    catch {
        $installState = $null
    }
}

$weAddedMembership = $false
if ($installState -and ($installState.PSObject.Properties.Name -contains 'perfLogUsersAddedByApp')) {
    $weAddedMembership = [bool]$installState.perfLogUsersAddedByApp
}

if ($weAddedMembership) {
    $removeConsent = $null
    if ($RemovePerfLogUsers) {
        $removeConsent = $true
    }
    elseif ($KeepPerfLogUsers) {
        $removeConsent = $false
    }
    elseif (-not $WhatIfPreference -and [Environment]::UserInteractive) {
        Write-Host ''
        Write-Host 'Hardware Live added this account to "Performance Log Users" for FPS capture.'
        $response = Read-Host 'Remove that membership now? [y/N]'
        $removeConsent = ($response -match '^(?i:y|yes)$')
    }
    else {
        Write-UninstallLog 'Performance Log Users rollback not asked (non-interactive, no -RemovePerfLogUsers/-KeepPerfLogUsers): leaving membership as-is.'
        $removeConsent = $false
    }

    if ($removeConsent) {
        Invoke-GuardedAction -Description 'remove the Performance Log Users membership this app added' -Action {
            # Prefer the SID install.ps1 recorded (the account the membership was actually
            # added for); fall back to the account running uninstall for older state files
            # written before this field existed.
            $targetSid = $null
            if ($installState -and ($installState.PSObject.Properties.Name -contains 'perfLogUsersUserSid')) {
                $targetSid = [string]$installState.perfLogUsersUserSid
            }

            if (-not $targetSid -and (Test-TargetUserSid -Sid $UserSid)) {
                # Older state file with no recorded SID: the invoking (pre-elevation) user is
                # a better guess than the elevated token's own identity, which may belong to a
                # different admin account entirely.
                $targetSid = $UserSid
            }

            if ($targetSid -and (Test-TargetUserSid -Sid $targetSid)) {
                $accountName = (New-Object System.Security.Principal.SecurityIdentifier($targetSid)).Translate([System.Security.Principal.NTAccount]).Value
            }
            else {
                $accountName = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).Name
            }

            try {
                Remove-LocalGroupMember -SID $PerfLogUsersSid -Member $accountName -ErrorAction Stop
                Write-UninstallLog "Removed $accountName from Performance Log Users."
            }
            catch {
                try {
                    $groupNtAccount = (New-Object System.Security.Principal.SecurityIdentifier($PerfLogUsersSid)).Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]
                    $group = [ADSI]"WinNT://./$groupNtAccount,group"
                    $shortName = $accountName.Split('\')[-1]
                    $group.Remove("WinNT://./$shortName,user")
                    Write-UninstallLog "Removed $accountName from Performance Log Users (via ADSI fallback)."
                }
                catch {
                    Write-UninstallLog "WARN: could not remove Performance Log Users membership: $($_.Exception.Message)"
                }
            }

            # On successful removal, clear the flag so a future reinstall starts clean.
            if (Test-Path -LiteralPath $InstallStatePath) {
                try {
                    $current = Get-Content -LiteralPath $InstallStatePath -Raw | ConvertFrom-Json
                    $updated = Merge-JsonObject -Existing $current -Updates @{ perfLogUsersAddedByApp = $false }
                    $json = $updated | ConvertTo-Json -Depth 10
                    [System.IO.File]::WriteAllText($InstallStatePath, $json, (New-Object System.Text.UTF8Encoding($false)))
                }
                catch {
                    Write-UninstallLog "WARN: could not update install-state.json after removing membership: $($_.Exception.Message)"
                }
            }
        }
    }
}
else {
    Write-UninstallLog 'Performance Log Users membership was not added by this app (or state is unknown); leaving it untouched.'
}

# ---------------------------------------------------------------------------
# Remove Program Files
# ---------------------------------------------------------------------------

Invoke-GuardedAction -Description "remove $ProgramFilesRoot" -Action {
    if (Test-Path -LiteralPath $ProgramFilesRoot) {
        Remove-Item -LiteralPath $ProgramFilesRoot -Recurse -Force
    }
}

# ---------------------------------------------------------------------------
# Remove ProgramData (optionally keep logs)
# ---------------------------------------------------------------------------

Invoke-GuardedAction -Description "remove $ProgramDataRoot (KeepLogs=$KeepLogs)" -Action {
    if (-not (Test-Path -LiteralPath $ProgramDataRoot)) {
        return
    }

    if ($KeepLogs) {
        Get-ChildItem -LiteralPath $ProgramDataRoot -Force |
            Where-Object { $_.FullName -ne $LogsDir } |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    }
    else {
        Remove-Item -LiteralPath $ProgramDataRoot -Recurse -Force
    }
}

# ---------------------------------------------------------------------------
# %LOCALAPPDATA%\HardwareLive (default: keep)
# ---------------------------------------------------------------------------

$userDataDecision = $null
if ($RemoveUserData) {
    $userDataDecision = $true
}
elseif ($KeepUserData) {
    $userDataDecision = $false
}
elseif (-not $WhatIfPreference -and [Environment]::UserInteractive) {
    Write-Host ''
    $response = Read-Host 'Remove your saved layouts/config/notes (%LOCALAPPDATA%\HardwareLive)? [y/N]'
    $userDataDecision = ($response -match '^(?i:y|yes)$')
}
else {
    Write-UninstallLog 'User data prompt not asked (non-interactive, no -KeepUserData/-RemoveUserData): defaulting to keep.'
    $userDataDecision = $false
}

if ($userDataDecision) {
    Invoke-GuardedAction -Description 'remove %LOCALAPPDATA%\HardwareLive for the target user' -Action {
        # Resolve via the captured invoking-user SID, not [Environment]::GetFolderPath, which
        # after self-elevation returns the *elevated* account's profile -- a different one
        # when a standard user supplied separate admin credentials at the UAC prompt.
        $localAppDataPath = $null
        $profilePath = Get-ProfileImagePathFromRegistry -Sid $UserSid
        if ($profilePath) {
            $localAppDataPath = Join-Path (Resolve-LocalAppDataFromProfilePath -ProfilePath $profilePath) 'HardwareLive'
        }
        else {
            $localAppDataPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'HardwareLive'
        }

        if (Test-Path -LiteralPath $localAppDataPath) {
            # Not Remove-Item -Recurse: it follows reparse points under it. This tree is
            # user-writable, so a planted junction could point the elevated delete anywhere.
            [System.IO.Directory]::Delete($localAppDataPath, $true)
        }
    }
}
else {
    Write-UninstallLog '%LOCALAPPDATA%\HardwareLive kept.'
}

Write-UninstallLog 'Uninstall complete.'

if ($Elevated -and [Environment]::UserInteractive) {
    Write-Host ''
    Write-Host 'Press Enter to close this window...'
    [void](Read-Host)
}

exit 0
