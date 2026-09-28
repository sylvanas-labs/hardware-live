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

$ProgramFilesRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'HardwareLive'
$ProgramDataRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'HardwareLive'
$LogsDir = Join-Path $ProgramDataRoot 'logs'
$InstallStatePath = Join-Path $ProgramDataRoot 'install-state.json'
$TaskFolder = '\HardwareLive\'
$StartMenuShortcutPath = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Hardware Live.lnk'
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
# Self-elevate. The %LOCALAPPDATA%\HardwareLive removal decision -- and, if the elevated child
# succeeds, the removal itself -- happens in THIS unelevated phase (see below): this process
# already IS the target user, so no profile-path impersonation is needed, and the elevated
# phase further down never touches %LOCALAPPDATA% at all (a symlink/junction planted there
# could otherwise redirect an elevated recursive delete anywhere that token can reach).
# ---------------------------------------------------------------------------

$currentSid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
if (-not $UserSid) {
    $UserSid = $currentSid
}

$script:IsUnelevatedProcess = -not (Test-IsAdministrator)
$userDataDecision = $null

if ($script:IsUnelevatedProcess) {
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

    if ($userDataDecision) { $RemoveUserData = $true } else { $KeepUserData = $true }
}

if (-not $Elevated -and $script:IsUnelevatedProcess) {
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
        # prompt, the elevated token may belong to a different account than the one whose
        # %LOCALAPPDATA%\HardwareLive we should be asking about.
        $argParts.Add('-UserSid "' + $currentSid + '"')
        if ($userDataDecision) { $argParts.Add('-RemoveUserData') } else { $argParts.Add('-KeepUserData') }
        if ($KeepLogs) { $argParts.Add('-KeepLogs') }
        if ($RemovePerfLogUsers) { $argParts.Add('-RemovePerfLogUsers') }
        if ($KeepPerfLogUsers) { $argParts.Add('-KeepPerfLogUsers') }

        $process = Start-Process -FilePath 'powershell.exe' -ArgumentList ($argParts -join ' ') -Verb RunAs -Wait -PassThru

        if ($userDataDecision -and $process.ExitCode -eq 0) {
            # Only after the elevated work actually succeeded -- deleting first and then
            # having the user cancel the UAC prompt (or the elevated run fail) would be data
            # loss with nothing left to show for it.
            $localAppDataPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'HardwareLive'
            if (Test-Path -LiteralPath $localAppDataPath) {
                try {
                    Remove-ItemReparseSafe -Path $localAppDataPath
                    Write-Host "Removed $localAppDataPath"
                }
                catch {
                    Write-Host "WARN: could not remove $localAppDataPath : $($_.Exception.Message)"
                }
            }
        }

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

Invoke-GuardedAction -Description "remove the Start menu shortcut $StartMenuShortcutPath" -Action {
    if (Test-Path -LiteralPath $StartMenuShortcutPath) {
        Remove-Item -LiteralPath $StartMenuShortcutPath -Force
    }
}

Invoke-GuardedAction -Description 'stop running hardware-live.exe / hl-sampler.exe' -Action {
    $processes = Get-Process -Name 'hardware-live', 'hl-sampler' -ErrorAction SilentlyContinue
    foreach ($process in $processes) {
        try { $process.Kill() } catch { }
    }
}

# ---------------------------------------------------------------------------
# Performance Log Users rollback -- only for SIDs a TRUSTED install-state.json says this app
# added. Same owner/trust check install.ps1 applies before trusting the file; an untrusted or
# unreadable state file removes NO group membership (never falls back to the elevated
# identity running this script, which is very likely just an admin, not the target user).
# ---------------------------------------------------------------------------

$installState = $null
$installStateTrusted = $false
if (Test-Path -LiteralPath $InstallStatePath) {
    $stateFileTrusted = $false
    try {
        # Check the CONTAINING DIRECTORY too, not just the leaf file: a root-is-a-junction
        # pointing at an otherwise admin-owned tree would pass a file-only check. This mirrors
        # install.ps1's own root-of-tree trust check (Step 4) before it writes install-state.json.
        $rootTrusted = (Test-Path -LiteralPath $ProgramDataRoot) -and (Test-DirectoryTrusted -Path $ProgramDataRoot)

        $stateItem = Get-Item -LiteralPath $InstallStatePath -Force
        if ($rootTrusted -and -not ($stateItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            $stateAcl = Get-Acl -LiteralPath $InstallStatePath
            $stateOwnerSid = $stateAcl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
            $stateFileTrusted = Test-AclOwnerIsAdmin -OwnerSid $stateOwnerSid
        }
    }
    catch {
        $stateFileTrusted = $false
    }

    if ($stateFileTrusted) {
        try {
            $installState = Get-Content -LiteralPath $InstallStatePath -Raw | ConvertFrom-Json
            $installStateTrusted = $true
        }
        catch {
            $installState = $null
            $installStateTrusted = $false
        }
    }
    else {
        Write-UninstallLog "WARN: $InstallStatePath is not a trusted admin-owned, non-reparse file; treating it as untrusted/tampered and removing NO Performance Log Users membership on its say-so."
    }
}

$sidsToRemove = @()
if ($installStateTrusted -and $installState -and ($installState.PSObject.Properties.Name -contains 'perfLogUsersAddedSids')) {
    $sidsToRemove = @($installState.perfLogUsersAddedSids | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
elseif ($installStateTrusted -and $installState -and
        ($installState.PSObject.Properties.Name -contains 'perfLogUsersAddedByApp') -and [bool]$installState.perfLogUsersAddedByApp -and
        ($installState.PSObject.Properties.Name -contains 'perfLogUsersUserSid')) {
    # Old state file predating perfLogUsersAddedSids: fall back to its single recorded SID,
    # but only because the file itself already passed the trust check above -- never fall back
    # to the identity running this (elevated, likely-admin) process.
    $legacySid = [string]$installState.perfLogUsersUserSid
    if (-not [string]::IsNullOrWhiteSpace($legacySid)) {
        $sidsToRemove = @($legacySid)
    }
}

if (@($sidsToRemove).Count -gt 0) {
    $removeConsent = $null
    if ($RemovePerfLogUsers) {
        $removeConsent = $true
    }
    elseif ($KeepPerfLogUsers) {
        $removeConsent = $false
    }
    elseif (-not $WhatIfPreference -and [Environment]::UserInteractive) {
        Write-Host ''
        Write-Host ("Hardware Live added {0} account(s) to `"Performance Log Users`" for FPS capture." -f @($sidsToRemove).Count)
        $response = Read-Host 'Remove that membership now? [y/N]'
        $removeConsent = ($response -match '^(?i:y|yes)$')
    }
    else {
        Write-UninstallLog 'Performance Log Users rollback not asked (non-interactive, no -RemovePerfLogUsers/-KeepPerfLogUsers): leaving membership as-is.'
        $removeConsent = $false
    }

    if ($removeConsent) {
        $remainingSids = New-Object System.Collections.Generic.List[string]

        foreach ($targetSid in $sidsToRemove) {
            Invoke-GuardedAction -Description "remove SID $targetSid from Performance Log Users (S-1-5-32-559)" -Action {
                $removedOk = $false
                try {
                    Remove-LocalGroupMember -SID $PerfLogUsersSid -Member $targetSid -ErrorAction Stop
                    Write-UninstallLog "Removed SID $targetSid from Performance Log Users."
                    $removedOk = $true
                }
                catch {
                    try {
                        $groupNtAccount = (New-Object System.Security.Principal.SecurityIdentifier($PerfLogUsersSid)).Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]
                        $group = [ADSI]"WinNT://./$groupNtAccount,group"
                        foreach ($memberComObject in @($group.Invoke('Members'))) {
                            $memberAdsi = [ADSI]$memberComObject
                            $sidBytes = $memberAdsi.InvokeGet('objectSid')
                            $memberSid = (New-Object System.Security.Principal.SecurityIdentifier($sidBytes, 0)).Value
                            if ([string]::Equals($memberSid, $targetSid, [System.StringComparison]::OrdinalIgnoreCase)) {
                                $group.Remove($memberAdsi.Path)
                                $removedOk = $true
                                Write-UninstallLog "Removed SID $targetSid from Performance Log Users (via ADSI fallback)."
                                break
                            }
                        }
                        if (-not $removedOk) {
                            Write-UninstallLog "WARN: SID $targetSid was not found in Performance Log Users membership; nothing to remove."
                            $removedOk = $true
                        }
                    }
                    catch {
                        Write-UninstallLog "WARN: could not remove SID $targetSid from Performance Log Users: $($_.Exception.Message)"
                    }
                }

                if (-not $removedOk) {
                    $remainingSids.Add($targetSid)
                }
            }
        }

        if (-not $WhatIfPreference -and (Test-Path -LiteralPath $InstallStatePath)) {
            try {
                $current = Get-Content -LiteralPath $InstallStatePath -Raw | ConvertFrom-Json
                $updated = Merge-JsonObject -Existing $current -Updates @{
                    perfLogUsersAddedSids  = @($remainingSids.ToArray())
                    perfLogUsersAddedByApp = (@($remainingSids).Count -gt 0)
                }
                $json = $updated | ConvertTo-Json -Depth 10
                [System.IO.File]::WriteAllText($InstallStatePath, $json, (New-Object System.Text.UTF8Encoding($false)))
            }
            catch {
                Write-UninstallLog "WARN: could not update install-state.json after removing membership: $($_.Exception.Message)"
            }
        }
    }
}
else {
    Write-UninstallLog 'No Performance Log Users membership recorded as added by this app (state absent/untrusted/empty); leaving membership untouched.'
}

# ---------------------------------------------------------------------------
# Remove Program Files
# ---------------------------------------------------------------------------

Invoke-GuardedAction -Description "remove $ProgramFilesRoot" -Action {
    if (Test-Path -LiteralPath $ProgramFilesRoot) {
        Remove-ItemReparseSafe -Path $ProgramFilesRoot
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
        # Remove-ItemReparseSafe refuses when its OWN root is a reparse point (by design -- see
        # its own doc comment), so a child here that is itself a junction/symlink must be
        # deleted as the link directly rather than passed in as that function's root.
        Get-ChildItem -LiteralPath $ProgramDataRoot -Force |
            Where-Object { $_.FullName -ne $LogsDir } |
            ForEach-Object {
                $childItem = $_
                try {
                    if ($childItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                        if ($childItem.PSIsContainer) {
                            [System.IO.Directory]::Delete($childItem.FullName, $false)
                        }
                        else {
                            [System.IO.File]::Delete($childItem.FullName)
                        }
                    }
                    else {
                        Remove-ItemReparseSafe -Path $childItem.FullName
                    }
                }
                catch {
                    Write-UninstallLog "WARN: could not remove $($childItem.FullName): $($_.Exception.Message)"
                }
            }
    }
    else {
        Remove-ItemReparseSafe -Path $ProgramDataRoot
    }
}

# ---------------------------------------------------------------------------
# %LOCALAPPDATA%\HardwareLive: removed only from the unelevated phase above, right after a
# successful elevated run. This elevated process never touches it -- if it started already
# elevated (no relaunch, so that phase never ran), just tell the user.
# ---------------------------------------------------------------------------

if ($RemoveUserData) {
    if ($Elevated) {
        # Relaunched child: the unelevated parent already removed (or will remove, after this
        # child exits 0) %LOCALAPPDATA%\HardwareLive itself -- nothing more to do here.
        Write-UninstallLog '%LOCALAPPDATA%\HardwareLive removal was handled by the unelevated parent process, not this elevated one.'
    }
    elseif ($WhatIfPreference) {
        # -WhatIf never relaunches/exits, so this same (genuinely unelevated) process falls
        # through to here too; the real removal is only ever simulated in the unelevated block
        # above, not repeated in this "as if elevated" section.
        Write-UninstallLog 'WHATIF: %LOCALAPPDATA%\HardwareLive removal was already simulated in the unelevated phase above; not simulated again here.'
    }
    else {
        Write-UninstallLog 'NOTE: %LOCALAPPDATA%\HardwareLive is only removed by the unelevated phase of this script, to avoid an elevated process following a user-controlled path. This process started already elevated (no relaunch occurred), so it was not touched -- remove it yourself if desired.'
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
