<#
.SYNOPSIS
    Installs Hardware Live: copies app/sampler/licenses into Program Files, creates the two
    per-user logon scheduled tasks, and offers Performance Log Users membership for FPS
    capture. Run from the extracted release zip (docs/SPEC.md Component 8).

.DESCRIPTION
    Run this from an unelevated shell; it self-elevates. Safe to re-run (upgrade path): stops
    the running tasks/processes first, re-copies, and never regresses the FPS permission flag
    from true to false.

.PARAMETER EnableFps
    Unattended: accept the Performance Log Users consent prompt without asking interactively.

.PARAMETER NoFps
    Unattended: decline the Performance Log Users consent prompt without asking interactively.

.PARAMETER Port
    Override the dashboard port. Defaults to the previous install's configured port, or 8790.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -WhatIf
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$EnableFps,
    [switch]$NoFps,
    [Nullable[int]]$Port = $null,

    # --- internal, used only when this script relaunches itself elevated ---
    [string]$UserSid,
    [switch]$Elevated
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# Force the native Windows PowerShell 5.1 Security module (Get-Acl/Set-Acl). Observed on this
# dev machine: a machine-wide PowerShell 7 install puts its own Microsoft.PowerShell.Security
# earlier in $env:PSModulePath; that module's assembly targets .NET, not .NET Framework, so
# auto-load finds it by name first and fails to import it silently -- Get-Acl/Set-Acl then
# report "not recognized" under powershell.exe even though the correct module is right there
# in $PSHOME. This is the ACL step's load-bearing dependency; import it explicitly rather than
# trust auto-load.
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop

$RepoToolsPath = Join-Path (Split-Path -Parent $PSCommandPath) 'tools\HardwareLive.InstallLib.psm1'
if (Test-Path -LiteralPath $RepoToolsPath) {
    Import-Module $RepoToolsPath -Force
}
else {
    # Packaged layout ships the module at the zip root next to install.ps1, not under tools\.
    Import-Module (Join-Path (Split-Path -Parent $PSCommandPath) 'HardwareLive.InstallLib.psm1') -Force
}

$ScriptRoot = Split-Path -Parent $PSCommandPath
$ProgramFilesRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'HardwareLive'
$ProgramDataRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'HardwareLive'
$LogsDir = Join-Path $ProgramDataRoot 'logs'
$InstallLogPath = Join-Path $LogsDir 'install.log'
$InstallStatePath = Join-Path $ProgramDataRoot 'install-state.json'
$TaskFolder = '\HardwareLive\'
$DefaultPort = 8790
$PerfLogUsersSid = 'S-1-5-32-559'

$script:LogLines = New-Object System.Collections.Generic.List[string]
# Only set once Step 4 has verified/established a trusted, protected $ProgramDataRoot. Before
# that, ProgramData's default (non-admin-writable-locked) ACL means an early elevated write
# (e.g. a preflight-failure log) could land inside an attacker-writable tree -- see
# Save-InstallLog below.
$script:ProgramDataRootTrusted = $false

function Write-InstallLog {
    param([string]$Message)

    $line = "[{0}] {1}" -f (Get-Date -Format 's'), $Message
    Write-Host $line
    $script:LogLines.Add($line)
}

function Save-InstallLog {
    # Never write the log during -WhatIf: the run must change nothing on disk.
    if ($WhatIfPreference) {
        return
    }

    if (-not (Test-Path -LiteralPath $LogsDir)) {
        if (-not $script:ProgramDataRootTrusted) {
            Write-Host "WARN: install log not written to disk (no trusted $ProgramDataRoot yet). See console output above for the full log."
            return
        }

        try {
            New-Item -ItemType Directory -Path $LogsDir -Force | Out-Null
        }
        catch {
            Write-Host "WARN: could not create $LogsDir for the install log: $($_.Exception.Message)"
            return
        }
    }

    try {
        $content = ($script:LogLines -join [Environment]::NewLine) + [Environment]::NewLine
        [System.IO.File]::WriteAllText($InstallLogPath, $content, (New-Object System.Text.UTF8Encoding($false)))
    }
    catch {
        Write-Host "WARN: could not write install log to $InstallLogPath : $($_.Exception.Message)"
    }
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
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
        Write-InstallLog "WHATIF: would $Description"
    }
}

function Test-IsAdministrator {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---------------------------------------------------------------------------
# Step 1: self-elevate, capturing the invoking (pre-elevation) user's SID first. FPS consent
# is also asked HERE, and -- if given -- %LOCALAPPDATA%\HardwareLive\config.json is written
# HERE too, while this process is still genuinely unelevated (see the comment below). The
# elevated phase (Step 6) never touches %LOCALAPPDATA%; it only records the consent decision
# into admin-owned %ProgramData%\HardwareLive\install-state.json.
# ---------------------------------------------------------------------------

if (-not $Elevated) {
    $currentSid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value

    if (-not $UserSid) {
        $UserSid = $currentSid
    }

    if ($currentSid.EndsWith('-500')) {
        Write-Host "WARN: the invoking account is the built-in Administrator (RID 500). Its per-user logon tasks and %LOCALAPPDATA% config will belong to that account, which most people never log into interactively."
    }

    if (-not (Test-IsAdministrator)) {
        $script:UnelevatedFpsConsentAsked = $false
        $script:UnelevatedFpsConsentValue = $false

        if ($EnableFps) {
            $script:UnelevatedFpsConsentAsked = $true
            $script:UnelevatedFpsConsentValue = $true
        }
        elseif ($NoFps) {
            $script:UnelevatedFpsConsentAsked = $true
            $script:UnelevatedFpsConsentValue = $false
        }
        elseif ($WhatIfPreference) {
            Write-Host "WHATIF: would ask for FPS consent (not asked under -WhatIf)."
        }
        elseif ([Environment]::UserInteractive) {
            Write-Host ''
            Write-Host 'FPS capture (optional) needs the target user to be in "Performance Log Users".'
            Write-Host 'That group lets the account start ETW trace sessions (used to read frame times via PresentMon).'
            Write-Host 'It does not grant admin rights and is reversible on uninstall.'
            $response = Read-Host 'Enable FPS capture and add this membership if needed? [y/N]'
            $script:UnelevatedFpsConsentAsked = $true
            $script:UnelevatedFpsConsentValue = ($response -match '^(?i:y|yes)$')
        }
        else {
            $script:UnelevatedFpsConsentAsked = $true
            $script:UnelevatedFpsConsentValue = $false
        }

        # Writing %LOCALAPPDATA%\HardwareLive\config.json from an elevated process would let a
        # symlink/junction planted in that user-controlled folder redirect the write to an
        # attacker-chosen target the elevated token can reach. This process is the invoking
        # user, unelevated -- the one and only safe place to make this write directly.
        if ($script:UnelevatedFpsConsentValue -and -not $WhatIfPreference) {
            $userConfigPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'HardwareLive\config.json'
            $existingUserConfig = $null
            if (Test-Path -LiteralPath $userConfigPath) {
                try { $existingUserConfig = Get-Content -LiteralPath $userConfigPath -Raw | ConvertFrom-Json } catch { $existingUserConfig = $null }
            }

            # Nested merge, not a wholesale fps replacement: a future fps.pins/denylist
            # living under the same "fps" key must survive a reinstall.
            $existingFps = $null
            if ($existingUserConfig -and ($existingUserConfig.PSObject.Properties.Name -contains 'fps')) {
                $existingFps = $existingUserConfig.fps
            }
            $mergedFps = Merge-JsonObject -Existing $existingFps -Updates @{ enabled = $true }
            $mergedConfig = Merge-JsonObject -Existing $existingUserConfig -Updates @{ fps = $mergedFps }
            $json = $mergedConfig | ConvertTo-Json -Depth 10
            Write-Utf8NoBom -Path $userConfigPath -Content $json
            Write-Host "FPS capture enabled: wrote $userConfigPath"
        }

        if ($WhatIfPreference) {
            # A -WhatIf run never elevates: it only prints what it would do, and every read
            # this script needs (registry, ACL, scheduled tasks) is readable unelevated.
            Write-Host "WHATIF: would self-elevate via UAC (Start-Process -Verb RunAs) to install for real."
        }
        else {
            Write-Host "Hardware Live needs administrator rights to install into Program Files, create the SYSTEM-run sampler task, and set protected ACLs. Requesting elevation now (UAC prompt)..."

            $argParts = New-Object System.Collections.Generic.List[string]
            $argParts.Add('-NoProfile')
            $argParts.Add('-ExecutionPolicy Bypass')
            $argParts.Add('-File "' + $PSCommandPath + '"')
            $argParts.Add('-Elevated')
            $argParts.Add('-UserSid "' + $currentSid + '"')
            if ($script:UnelevatedFpsConsentAsked -and $script:UnelevatedFpsConsentValue) {
                $argParts.Add('-EnableFps')
            }
            elseif ($script:UnelevatedFpsConsentAsked) {
                $argParts.Add('-NoFps')
            }
            if ($null -ne $Port) { $argParts.Add("-Port $Port") }

            $process = Start-Process -FilePath 'powershell.exe' -ArgumentList ($argParts -join ' ') -Verb RunAs -Wait -PassThru
            exit $process.ExitCode
        }
    }
}

if (-not $UserSid) {
    $UserSid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
}

if (-not (Test-TargetUserSid -Sid $UserSid)) {
    Write-Error "FAIL: '$UserSid' is not a supported user account SID (must be a local/domain account S-1-5-21-... or a Microsoft Entra ID account S-1-12-1-...). hl-sampler.exe would reject it at every logon. Aborting."
    exit 1
}

Write-InstallLog "Hardware Live install starting. Target user SID: $UserSid. WhatIf: $WhatIfPreference."

# ---------------------------------------------------------------------------
# Step 2: preflight
# ---------------------------------------------------------------------------

$script:PreflightFailed = $false

function Write-PreflightResult {
    param(
        [Parameter(Mandatory = $true)][string]$Check,
        [Parameter(Mandatory = $true)][ValidateSet('PASS', 'WARN', 'FAIL')][string]$Result,
        [string]$Detail = ''
    )

    $line = "[$Result] $Check"
    if ($Detail) {
        $line += " -- $Detail"
    }

    Write-InstallLog $line

    if ($Result -eq 'FAIL') {
        $script:PreflightFailed = $true
    }
}

# 1. Windows 10 1809+ (build 17763) x64
$osVersion = [System.Environment]::OSVersion.Version
$is64Bit = [System.Environment]::Is64BitOperatingSystem
if ($osVersion.Build -ge 17763 -and $is64Bit) {
    Write-PreflightResult -Check 'Windows 10 1809+ x64' -Result 'PASS' -Detail "build $($osVersion.Build), 64-bit"
}
else {
    Write-PreflightResult -Check 'Windows 10 1809+ x64' -Result 'FAIL' -Detail "build $($osVersion.Build), 64-bit=$is64Bit"
}

# 2. PawnIO
if (Test-Path -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\PawnIO') {
    Write-PreflightResult -Check 'PawnIO driver' -Result 'PASS'
}
else {
    Write-PreflightResult -Check 'PawnIO driver' -Result 'WARN' -Detail 'Not installed. CPU/board sensors need it. We never install it for you -- run: winget install namazso.PawnIO'
}

# 3. Edge present
$edgePaths = @(
    (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Microsoft\Edge\Application\msedge.exe'),
    (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft\Edge\Application\msedge.exe')
)
$edgeFound = $false
foreach ($edgePath in $edgePaths) {
    if ($edgePath -and (Test-Path -LiteralPath $edgePath)) {
        $edgeFound = $true
        break
    }
}
if ($edgeFound) {
    Write-PreflightResult -Check 'Microsoft Edge present' -Result 'PASS'
}
else {
    Write-PreflightResult -Check 'Microsoft Edge present' -Result 'WARN' -Detail 'Not found. The dashboard window will fall back to your default browser instead of an app window.'
}

# 4. Effective port free
$effectivePort = $DefaultPort
$existingConfigPath = Join-Path $ProgramFilesRoot 'app\config.json'
if (Test-Path -LiteralPath $existingConfigPath) {
    try {
        $existingConfig = Get-Content -LiteralPath $existingConfigPath -Raw | ConvertFrom-Json
        if ($existingConfig -and $existingConfig.PSObject.Properties.Name -contains 'port') {
            $effectivePort = [int]$existingConfig.port
        }
    }
    catch {
        # Malformed existing config: fall back to the default, matching PortConfiguration's
        # own "never let a bad config.json block startup" contract.
    }
}
if ($null -ne $Port) {
    $effectivePort = $Port
}

$portInUse = $false
try {
    $existingConnections = Get-NetTCPConnection -LocalPort $effectivePort -ErrorAction SilentlyContinue
    $portInUse = ($null -ne $existingConnections -and $existingConnections.Count -gt 0)
}
catch {
    $portInUse = $false
}

if ($portInUse) {
    Write-PreflightResult -Check "Port $effectivePort free" -Result 'WARN' -Detail 'Already in use. This is expected on an upgrade (the old instance is about to be stopped); otherwise pick a different -Port.'
}
else {
    Write-PreflightResult -Check "Port $effectivePort free" -Result 'PASS'
}

if ($script:PreflightFailed) {
    Write-InstallLog 'FAIL: preflight failed. Aborting before making any changes.'
    Save-InstallLog
    exit 1
}

# ---------------------------------------------------------------------------
# Step 3: stop previous install, copy files, set + verify protected ACL
# ---------------------------------------------------------------------------

function Stop-HardwareLiveTasksAndProcesses {
    Invoke-GuardedAction -Description 'stop and unregister existing HardwareLive scheduled tasks' -Action {
        $existingTasks = Get-ScheduledTask -TaskPath $TaskFolder -ErrorAction SilentlyContinue
        foreach ($task in $existingTasks) {
            try { Stop-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction SilentlyContinue } catch { }
            Unregister-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -Confirm:$false -ErrorAction SilentlyContinue
        }
    }

    Invoke-GuardedAction -Description 'stop running hardware-live.exe / hl-sampler.exe and wait for exit' -Action {
        $processes = Get-Process -Name 'hardware-live', 'hl-sampler' -ErrorAction SilentlyContinue
        foreach ($process in $processes) {
            try { $process.Kill() } catch { }
        }

        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline) {
            $stillRunning = Get-Process -Name 'hardware-live', 'hl-sampler' -ErrorAction SilentlyContinue
            if (-not $stillRunning) {
                break
            }

            Start-Sleep -Milliseconds 500
        }
    }
}

function Set-ProtectedAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    $sidSystem = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $sidAdmins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $sidUsers = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-545')

    $items = @(Get-Item -LiteralPath $Path -Force) + @(Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue)

    foreach ($item in $items) {
        # A user-planted junction/symlink here would make this elevated, recursive re-owning
        # walk follow it and re-ACL an attacker-chosen target tree. Refuse rather than follow.
        if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            throw "$($item.FullName) is a reparse point (junction/symlink). Refusing to set ACLs through it -- remove it manually and re-run."
        }

        if ($item.PSIsContainer) {
            $acl = New-Object System.Security.AccessControl.DirectorySecurity
            $inheritanceFlags = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
        }
        else {
            $acl = New-Object System.Security.AccessControl.FileSecurity
            $inheritanceFlags = [System.Security.AccessControl.InheritanceFlags]::None
        }

        $acl.SetAccessRuleProtection($true, $false)
        $acl.SetOwner($sidAdmins)

        $propagation = [System.Security.AccessControl.PropagationFlags]::None
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sidSystem, 'FullControl', $inheritanceFlags, $propagation, 'Allow')))
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sidAdmins, 'FullControl', $inheritanceFlags, $propagation, 'Allow')))
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sidUsers, 'ReadAndExecute', $inheritanceFlags, $propagation, 'Allow')))

        Set-Acl -LiteralPath $item.FullName -AclObject $acl
    }
}

function Test-ProtectedAclVerified {
    param([Parameter(Mandatory = $true)][string]$Path)

    $items = @(Get-Item -LiteralPath $Path -Force) + @(Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue)

    foreach ($item in $items) {
        $acl = Get-Acl -LiteralPath $item.FullName
        $ownerSid = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
        if (-not (Test-AclOwnerIsAdmin -OwnerSid $ownerSid)) {
            Write-InstallLog "ACL FAIL: $($item.FullName) owner SID is '$ownerSid', expected an admin principal."
            return $false
        }

        $accessRules = @()
        foreach ($rule in $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
            $accessRules += [pscustomobject]@{
                IdentitySid        = $rule.IdentityReference.Value
                AccessControlType  = $rule.AccessControlType.ToString()
                FileSystemRights   = [int]$rule.FileSystemRights
            }
        }

        if (Test-AclHasDisallowedWrite -AccessRules $accessRules) {
            Write-InstallLog "ACL FAIL: $($item.FullName) grants a non-admin principal a write-capable right."
            return $false
        }
    }

    return $true
}

Stop-HardwareLiveTasksAndProcesses

Invoke-GuardedAction -Description "copy app/sampler/licenses into $ProgramFilesRoot" -Action {
    if (-not (Test-Path -LiteralPath $ProgramFilesRoot)) {
        New-Item -ItemType Directory -Path $ProgramFilesRoot -Force | Out-Null
    }

    foreach ($subfolder in @('app', 'sampler', 'licenses')) {
        $source = Join-Path $ScriptRoot $subfolder
        if (-not (Test-Path -LiteralPath $source)) {
            Write-InstallLog "WARN: $source not present in the package -- skipping $subfolder."
            continue
        }

        $destination = Join-Path $ProgramFilesRoot $subfolder
        # /MIR mirrors (deletes anything in $destination that's not in $source too, so an
        # upgrade never leaves stale files); /COPY:DAT (data+attributes+timestamps only --
        # never /SEC or /COPYALL, which would import the extracted zip's own user-writable ACL
        # into what's about to become a protected, admin-only Program Files tree).
        $robocopyOutput = & robocopy $source $destination /MIR /COPY:DAT /R:2 /W:2 /NFL /NDL /NJH /NJS 2>&1
        if ($LASTEXITCODE -ge 8) {
            Write-InstallLog "FAIL: robocopy $subfolder failed (exit $LASTEXITCODE): $robocopyOutput"
            throw "robocopy failed for $subfolder (exit $LASTEXITCODE)"
        }

        Write-InstallLog "Copied $subfolder (robocopy exit $LASTEXITCODE)."
    }
}

Invoke-GuardedAction -Description "set protected ACL on $ProgramFilesRoot (SYSTEM+Administrators FullControl, Users ReadAndExecute)" -Action {
    Set-ProtectedAcl -Path $ProgramFilesRoot

    if (-not (Test-ProtectedAclVerified -Path $ProgramFilesRoot)) {
        Write-InstallLog "FAIL: ACL verification failed after setting it. Rolling back the copy."
        # Remove-ItemReparseSafe throws (a terminating error) on failure; -ErrorAction would not
        # suppress that, so catch explicitly to guarantee Save-InstallLog and the intended
        # "ACL verification failed" error still surface even if the rollback delete itself fails.
        try {
            Remove-ItemReparseSafe -Path $ProgramFilesRoot
        }
        catch {
            Write-InstallLog "WARN: rollback delete of $ProgramFilesRoot failed: $($_.Exception.Message)"
        }
        Save-InstallLog
        throw 'ACL verification failed; install rolled back.'
    }

    Write-InstallLog "ACL verified: no non-admin principal has write access under $ProgramFilesRoot."
}

# ---------------------------------------------------------------------------
# Step 4: %ProgramData%\HardwareLive (root + logs) protected ACL
# ---------------------------------------------------------------------------

Invoke-GuardedAction -Description "create $ProgramDataRoot and $LogsDir with a protected ACL" -Action {
    if (Test-Path -LiteralPath $ProgramDataRoot) {
        $rootIsTrusted = $false
        try {
            $rootIsTrusted = Test-DirectoryTrusted -Path $ProgramDataRoot
        }
        catch {
            $rootIsTrusted = $false
        }

        if (-not $rootIsTrusted) {
            # ProgramData's default ACL lets any user create files/folders there (CREATOR
            # OWNER FullControl), so a non-admin could have planted this whole tree --
            # including an install-state.json with perfLogUsersAddedByApp=true -- before
            # install ever ran. Never re-own a pre-existing untrusted tree in place (that would
            # make the planted content look trustworthy afterward); rename it aside instead.
            $untrustedName = "HardwareLive.untrusted-{0}" -f (Get-Date -Format 'yyyyMMddHHmmssfff')
            $untrustedPath = Join-Path (Split-Path -Parent $ProgramDataRoot) $untrustedName
            Write-InstallLog "WARN: $ProgramDataRoot is not a trusted admin-owned/protected tree; renaming it aside to $untrustedPath rather than reusing or re-owning it."
            try {
                [System.IO.Directory]::Move($ProgramDataRoot, $untrustedPath)
            }
            catch {
                Write-InstallLog "FAIL: could not rename aside the untrusted $ProgramDataRoot : $($_.Exception.Message)"
                Save-InstallLog
                throw "Could not rename aside untrusted $ProgramDataRoot; aborting rather than reuse it."
            }
        }
    }

    if (-not (Test-Path -LiteralPath $ProgramDataRoot)) {
        # Atomic: the protected ACL is baked in at creation (DirectoryInfo.Create(security)),
        # closing the create-then-Set-Acl window a planted junction/race could otherwise
        # exploit at exactly this path.
        New-ProtectedDirectoryAtomic -Path $ProgramDataRoot
    }
    $script:ProgramDataRootTrusted = $true

    if (-not (Test-Path -LiteralPath $LogsDir)) {
        # Created inside an already-protected, non-admin-writable parent: no equivalent race
        # exists here.
        New-Item -ItemType Directory -Path $LogsDir -Force | Out-Null
    }

    # Defense in depth: explicitly re-set owner+ACL on the whole tree (root + logs) regardless
    # of which branch above ran, matching what verification below checks.
    Set-ProtectedAcl -Path $ProgramDataRoot

    if (-not (Test-ProtectedAclVerified -Path $ProgramDataRoot)) {
        Write-InstallLog "FAIL: ACL verification failed on $ProgramDataRoot."
        Save-InstallLog
        throw 'ProgramData ACL verification failed.'
    }

    Write-InstallLog "ACL verified under $ProgramDataRoot."
}

# ---------------------------------------------------------------------------
# Step 5: scheduled tasks
# ---------------------------------------------------------------------------

$targetSidObject = New-Object System.Security.Principal.SecurityIdentifier($UserSid)
$targetAccountName = $null
try {
    $targetAccountName = $targetSidObject.Translate([System.Security.Principal.NTAccount]).Value
}
catch {
    Write-Error "FAIL: could not resolve account name for SID $UserSid (account may no longer exist). $($_.Exception.Message)"
    Save-InstallLog
    exit 1
}

Invoke-GuardedAction -Description "register the HardwareLive\Sampler task (SYSTEM, AtLogOn for $targetAccountName)" -Action {
    $samplerExe = Join-Path $ProgramFilesRoot 'sampler\hl-sampler.exe'
    $action = New-ScheduledTaskAction -Execute $samplerExe -Argument "--user-sid $UserSid"
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $targetAccountName
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
        -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero)

    Register-ScheduledTask -TaskName 'Sampler' -TaskPath $TaskFolder -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force | Out-Null
}

Invoke-GuardedAction -Description "register the HardwareLive\App task (Limited, AtLogOn+10s for $targetAccountName)" -Action {
    $appExe = Join-Path $ProgramFilesRoot 'app\hardware-live.exe'
    $action = New-ScheduledTaskAction -Execute $appExe
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $targetAccountName
    $trigger.Delay = 'PT10S'
    $principal = New-ScheduledTaskPrincipal -UserId $targetAccountName -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
        -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero)

    Register-ScheduledTask -TaskName 'App' -TaskPath $TaskFolder -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force | Out-Null
}

# ---------------------------------------------------------------------------
# Step 6: FPS consent record + Performance Log Users membership (elevated). The consent
# decision itself, and the %LOCALAPPDATA%\HardwareLive\config.json write, happen in the
# unelevated phase above (Step 1) on the normal (self-elevating) path. This block is reached
# here only to (a) act on the forwarded -EnableFps/-NoFps decision, or (b) ask directly when
# this process started already elevated (no relaunch -- Step 1's unelevated branch never ran).
# Either way, this elevated process never writes %LOCALAPPDATA%; it only records the decision
# into admin-owned install-state.json, which the app itself applies on first start.
# ---------------------------------------------------------------------------

function Test-PerfLogUsersMember {
    param([Parameter(Mandatory = $true)][string]$AccountName)

    try {
        $member = Get-LocalGroupMember -SID $PerfLogUsersSid -ErrorAction Stop |
            Where-Object { $_.SID.Value -eq $UserSid }
        return $null -ne $member
    }
    catch {
        # Get-LocalGroupMember -SID can throw if the group holds an orphaned/unresolvable SID
        # (a known 5.1 issue). Fall back to the ADSI WinNT provider, comparing by SID (never by
        # display name, which can be renamed/localized/ambiguous across domain vs local).
        try {
            $groupNtAccount = (New-Object System.Security.Principal.SecurityIdentifier($PerfLogUsersSid)).Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]
            $group = [ADSI]"WinNT://./$groupNtAccount,group"
            $isMember = $false
            foreach ($memberComObject in @($group.Invoke('Members'))) {
                $sidBytes = ([ADSI]$memberComObject).InvokeGet('objectSid')
                $memberSid = (New-Object System.Security.Principal.SecurityIdentifier($sidBytes, 0)).Value
                if ([string]::Equals($memberSid, $UserSid, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $isMember = $true
                    break
                }
            }
            return $isMember
        }
        catch {
            Write-InstallLog "WARN: could not determine Performance Log Users membership: $($_.Exception.Message)"
            return $false
        }
    }
}

$fpsConsent = $null
if ($EnableFps) {
    $fpsConsent = $true
}
elseif ($NoFps) {
    $fpsConsent = $false
}
elseif (-not $WhatIfPreference -and [Environment]::UserInteractive) {
    Write-Host ''
    Write-Host 'FPS capture (optional) needs the target user to be in "Performance Log Users".'
    Write-Host 'That group lets the account start ETW trace sessions (used to read frame times via PresentMon).'
    Write-Host 'It does not grant admin rights and is reversible on uninstall.'
    $response = Read-Host 'Enable FPS capture and add this membership if needed? [y/N]'
    $fpsConsent = ($response -match '^(?i:y|yes)$')
}
else {
    Write-InstallLog 'FPS consent not asked (non-interactive, no -EnableFps/-NoFps): leaving FPS off, membership untouched.'
    $fpsConsent = $false
}

$alreadyMember = Test-PerfLogUsersMember -AccountName $targetAccountName
$addedThisRun = $false

if ($fpsConsent -and -not $alreadyMember) {
    Invoke-GuardedAction -Description "add $targetAccountName to Performance Log Users (S-1-5-32-559)" -Action {
        Add-LocalGroupMember -SID $PerfLogUsersSid -Member $targetAccountName
        Write-InstallLog "Added $targetAccountName to Performance Log Users. This takes effect at next logon."
    }
    $addedThisRun = $true
}
elseif ($fpsConsent -and $alreadyMember) {
    Write-InstallLog "$targetAccountName is already a member of Performance Log Users; nothing to add."
}

# install-state.json: admin-owned in ProgramData, so the user can't tamper with the flags that
# control whether uninstall offers to remove a membership it didn't add.
$existingState = $null
if (Test-Path -LiteralPath $InstallStatePath) {
    $stateAcl = Get-Acl -LiteralPath $InstallStatePath
    $stateOwnerSid = $stateAcl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
    if (Test-AclOwnerIsAdmin -OwnerSid $stateOwnerSid) {
        try {
            $existingState = Get-Content -LiteralPath $InstallStatePath -Raw | ConvertFrom-Json
        }
        catch {
            $existingState = $null
        }
    }
    else {
        Write-InstallLog "WARN: $InstallStatePath is not admin-owned (owner SID: $stateOwnerSid); treating as absent/tampered and overwriting."
    }
}

$existingFlag = $null
if ($existingState -and ($existingState.PSObject.Properties.Name -contains 'perfLogUsersAddedByApp')) {
    $existingFlag = [bool]$existingState.perfLogUsersAddedByApp
}

$existingSids = $null
if ($existingState -and ($existingState.PSObject.Properties.Name -contains 'perfLogUsersAddedSids')) {
    $existingSids = @($existingState.perfLogUsersAddedSids | ForEach-Object { [string]$_ })
}

$legacySid = $null
if ($existingState -and ($existingState.PSObject.Properties.Name -contains 'perfLogUsersUserSid')) {
    $legacySid = [string]$existingState.perfLogUsersUserSid
}

$addedSidThisRun = $null
if ($addedThisRun) {
    $addedSidThisRun = $UserSid
}

$mergedFlag = Merge-PerfLogUsersAddedFlag -ExistingValue $existingFlag -AddedMembershipThisRun $addedThisRun -AlreadyMemberThisRun ($fpsConsent -and $alreadyMember)
$mergedSids = Resolve-PerfLogUsersAddedSids -ExistingSids $existingSids -AddedSidThisRun $addedSidThisRun -LegacyAddedByApp $existingFlag -LegacySid $legacySid

Invoke-GuardedAction -Description "write $InstallStatePath (perfLogUsersAddedByApp=$mergedFlag, perfLogUsersAddedSids=$(@($mergedSids).Count) SID(s), fpsConsent=$fpsConsent)" -Action {
    $stateUpdates = @{
        perfLogUsersAddedByApp = $mergedFlag
        perfLogUsersAddedSids  = @($mergedSids)
        # Kept for back-compat readers only; perfLogUsersAddedSids (monotonic union) is what
        # uninstall actually acts on now -- this scalar is no longer trusted for removal.
        perfLogUsersUserSid    = $UserSid
        fpsConsent             = $fpsConsent
        lastInstallUtc         = (Get-Date).ToUniversalTime().ToString('o')
    }
    $mergedState = Merge-JsonObject -Existing $existingState -Updates $stateUpdates
    $json = $mergedState | ConvertTo-Json -Depth 10
    Write-Utf8NoBom -Path $InstallStatePath -Content $json
    Set-ProtectedAcl -Path $InstallStatePath
}

if ($fpsConsent) {
    Write-InstallLog "FPS consent recorded. If %LOCALAPPDATA%\HardwareLive\config.json wasn't already written directly (only possible from the unelevated pre-UAC phase), Hardware Live will enable FPS capture automatically the first time the app starts."
}

# ---------------------------------------------------------------------------
# Step 7: start tasks, wait for health, print summary
# ---------------------------------------------------------------------------

Invoke-GuardedAction -Description 'start the Sampler and App scheduled tasks now' -Action {
    Start-ScheduledTask -TaskName 'Sampler' -TaskPath $TaskFolder
    Start-ScheduledTask -TaskName 'App' -TaskPath $TaskFolder

    $healthUrl = "http://127.0.0.1:$effectivePort/api/health"
    $deadline = (Get-Date).AddSeconds(30)
    $healthy = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -eq 200) {
                $healthy = $true
                break
            }
        }
        catch {
            # Not up yet; keep polling until the deadline.
        }

        Start-Sleep -Milliseconds 500
    }

    if ($healthy) {
        Write-InstallLog "Dashboard is responding at $healthUrl"
    }
    else {
        Write-InstallLog "WARN: dashboard did not respond at $healthUrl within 30s. Check Task Scheduler (\HardwareLive\) and $LogsDir."
    }
}

Write-InstallLog '--- Install summary ---'
Write-InstallLog "Installed to: $ProgramFilesRoot"
Write-InstallLog "Dashboard: http://127.0.0.1:$effectivePort/"
Write-InstallLog "Target user: $targetAccountName ($UserSid)"
Write-InstallLog "FPS capture: $fpsConsent (Performance Log Users membership takes effect at next logon if just added)"
Write-InstallLog "Scheduled tasks: $TaskFolder Sampler, $TaskFolder App"

Save-InstallLog

if ($Elevated -and [Environment]::UserInteractive) {
    Write-Host ''
    Write-Host 'Press Enter to close this window...'
    [void](Read-Host)
}

exit 0
