<#
.SYNOPSIS
    Plain-assert unit tests for tools/HardwareLive.InstallLib.psm1. No Pester dependency, no
    admin rights, no real registry/filesystem ACL access -- every function under test takes
    its "machine state" as an injected parameter (fake SID/path map, fake ACE list, ...).

.DESCRIPTION
    Exit 0 on all-pass, exit 1 on any failure (prints every failure before exiting).
    PowerShell 5.1 compatible. ASCII only.
#>

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$moduleRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $moduleRoot 'HardwareLive.InstallLib.psm1') -Force

$script:failures = New-Object System.Collections.Generic.List[string]
$script:passCount = 0

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][bool]$Condition
    )

    if ($Condition) {
        $script:passCount++
    }
    else {
        $script:failures.Add("FAIL: $Name")
    }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        $Expected,
        $Actual
    )

    if ($Expected -eq $Actual) {
        $script:passCount++
    }
    else {
        $script:failures.Add("FAIL: $Name (expected [$Expected], got [$Actual])")
    }
}

# --- Test-TargetUserSid ------------------------------------------------------

Assert-True 'local user SID accepted' (Test-TargetUserSid -Sid 'S-1-5-21-1111111111-2222222222-3333333333-1001')
Assert-True 'Entra ID (Azure AD) user SID accepted' (Test-TargetUserSid -Sid 'S-1-12-1-1234567890-1234567890-1234567890-1234567890')
Assert-True 'service SID rejected' (-not (Test-TargetUserSid -Sid 'S-1-5-18'))
Assert-True 'other S-1-12 authority form rejected' (-not (Test-TargetUserSid -Sid 'S-1-12-2-1-2-3-4'))
Assert-True 'LocalSystem SID rejected (S-1-5-18)' (-not (Test-TargetUserSid -Sid 'S-1-5-18'))
Assert-True 'LocalService SID rejected (S-1-5-19)' (-not (Test-TargetUserSid -Sid 'S-1-5-19'))
Assert-True 'NetworkService SID rejected (S-1-5-20)' (-not (Test-TargetUserSid -Sid 'S-1-5-20'))
Assert-True 'second Entra ID SID accepted (S-1-12-1-...)' (Test-TargetUserSid -Sid 'S-1-12-1-1111111111-2222222222-3333333333-4444444444')
Assert-True 'malformed SID rejected' (-not (Test-TargetUserSid -Sid 'not-a-sid'))
Assert-True 'empty SID rejected' (-not (Test-TargetUserSid -Sid ''))

# --- Resolve-ProfilePathFromSid ----------------------------------------------

$profileMap = @{
    'S-1-5-21-1-2-3-1001' = 'C:\Users\alice'
    'S-1-5-21-1-2-3-1002' = 'C:\Users\bob'
}
Assert-Equal 'known SID resolves to profile path' 'C:\Users\alice' (Resolve-ProfilePathFromSid -Sid 'S-1-5-21-1-2-3-1001' -ProfileMap $profileMap)
Assert-Equal 'unknown SID resolves to null' $null (Resolve-ProfilePathFromSid -Sid 'S-1-5-21-9-9-9-9999' -ProfileMap $profileMap)
Assert-Equal 'LocalAppData path joins correctly' 'C:\Users\alice\AppData\Local' (Resolve-LocalAppDataFromProfilePath -ProfilePath 'C:\Users\alice')

# --- Merge-PerfLogUsersAddedFlag (monotonic) --------------------------------

Assert-Equal 'first install, added -> true' $true (Merge-PerfLogUsersAddedFlag -ExistingValue $null -AddedMembershipThisRun $true -AlreadyMemberThisRun $false)
Assert-Equal 'first install, already member -> false' $false (Merge-PerfLogUsersAddedFlag -ExistingValue $null -AddedMembershipThisRun $false -AlreadyMemberThisRun $true)
Assert-Equal 'reinstall, existing true, already member -> stays true (monotonic)' $true (Merge-PerfLogUsersAddedFlag -ExistingValue $true -AddedMembershipThisRun $false -AlreadyMemberThisRun $true)
Assert-Equal 'reinstall, existing true, added again -> stays true' $true (Merge-PerfLogUsersAddedFlag -ExistingValue $true -AddedMembershipThisRun $true -AlreadyMemberThisRun $false)
Assert-Equal 'reinstall, existing false, already member -> stays false (present key untouched)' $false (Merge-PerfLogUsersAddedFlag -ExistingValue $false -AddedMembershipThisRun $false -AlreadyMemberThisRun $true)
Assert-Equal 'reinstall, FPS not offered this run, existing true -> untouched' $true (Merge-PerfLogUsersAddedFlag -ExistingValue $true -AddedMembershipThisRun $false -AlreadyMemberThisRun $false)
Assert-Equal 'reinstall, FPS not offered this run, existing false -> untouched' $false (Merge-PerfLogUsersAddedFlag -ExistingValue $false -AddedMembershipThisRun $false -AlreadyMemberThisRun $false)
Assert-Equal 'reinstall, FPS not offered, existing absent -> false' $false (Merge-PerfLogUsersAddedFlag -ExistingValue $null -AddedMembershipThisRun $false -AlreadyMemberThisRun $false)

# --- Merge-JsonObject (preserve unknown keys) -------------------------------

$existingJson = '{"port":9001,"customKey":"keepme","openWindowOnStart":false}' | ConvertFrom-Json
$updates = @{ openWindowOnStart = $true; fps = @{ enabled = $true } }
$merged = Merge-JsonObject -Existing $existingJson -Updates $updates
Assert-Equal 'merge preserves unknown key' 'keepme' $merged['customKey']
Assert-Equal 'merge preserves untouched key' 9001 $merged['port']
Assert-Equal 'merge applies update' $true $merged['openWindowOnStart']
Assert-True 'merge applies new nested key' ($null -ne $merged['fps'])

$mergedFromNull = Merge-JsonObject -Existing $null -Updates @{ a = 1 }
Assert-Equal 'merge from null existing still applies update' 1 $mergedFromNull['a']

# --- Test-AclHasDisallowedWrite ---------------------------------------------

$fullControl = [int][System.Security.AccessControl.FileSystemRights]::FullControl
$readExecute = [int][System.Security.AccessControl.FileSystemRights]::ReadAndExecute
$modify = [int][System.Security.AccessControl.FileSystemRights]::Modify

$safeAcl = @(
    [pscustomobject]@{ IdentityReference = 'NT AUTHORITY\SYSTEM'; AccessControlType = 'Allow'; FileSystemRights = $fullControl }
    [pscustomobject]@{ IdentityReference = 'BUILTIN\Administrators'; AccessControlType = 'Allow'; FileSystemRights = $fullControl }
    [pscustomobject]@{ IdentityReference = 'BUILTIN\Users'; AccessControlType = 'Allow'; FileSystemRights = $readExecute }
)
Assert-True 'safe ACL (SYSTEM+Administrators FullControl, Users ReadAndExecute) has no disallowed write' (-not (Test-AclHasDisallowedWrite -AccessRules $safeAcl))

$dangerousAcl = $safeAcl + @([pscustomobject]@{ IdentityReference = 'BUILTIN\Users'; AccessControlType = 'Allow'; FileSystemRights = $modify })
Assert-True 'ACL granting Users Modify is flagged as disallowed write' (Test-AclHasDisallowedWrite -AccessRules $dangerousAcl)

$everyoneAcl = @([pscustomobject]@{ IdentityReference = 'Everyone'; AccessControlType = 'Allow'; FileSystemRights = $fullControl })
Assert-True 'Everyone FullControl is flagged as disallowed write' (Test-AclHasDisallowedWrite -AccessRules $everyoneAcl)

$denyAcl = @([pscustomobject]@{ IdentityReference = 'BUILTIN\Users'; AccessControlType = 'Deny'; FileSystemRights = $fullControl })
Assert-True 'a Deny ACE never counts as a disallowed write' (-not (Test-AclHasDisallowedWrite -AccessRules $denyAcl))

Assert-True 'empty ACL has no disallowed write' (-not (Test-AclHasDisallowedWrite -AccessRules @()))

# --- Test-AclOwnerIsAdmin ----------------------------------------------------

Assert-True 'SYSTEM owner is admin' (Test-AclOwnerIsAdmin -Owner 'NT AUTHORITY\SYSTEM')
Assert-True 'Administrators owner is admin' (Test-AclOwnerIsAdmin -Owner 'BUILTIN\Administrators')
Assert-True 'invoking-user owner is not admin' (-not (Test-AclOwnerIsAdmin -Owner 'CONTOSO\alice'))
Assert-True 'CREATOR OWNER owner is not admin' (-not (Test-AclOwnerIsAdmin -Owner 'CREATOR OWNER'))

# --- Summary -----------------------------------------------------------------

Write-Host ("test-installlib.ps1: {0} passed, {1} failed" -f $script:passCount, $script:failures.Count)

if ($script:failures.Count -gt 0) {
    foreach ($failure in $script:failures) {
        Write-Host $failure
    }

    exit 1
}

exit 0
