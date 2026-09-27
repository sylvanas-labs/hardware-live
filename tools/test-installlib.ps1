<#
.SYNOPSIS
    Plain-assert unit tests for tools/HardwareLive.InstallLib.psm1. No Pester dependency, no
    admin rights, no real registry/filesystem ACL access -- every function under test takes
    its "machine state" as an injected parameter (fake SID/path map, fake ACE list, ...).
    Remove-ItemReparseSafe is the one exception: it is exercised against real temp-directory
    junctions, which PowerShell 5.1 can create without administrator rights.

.DESCRIPTION
    Exit 0 on all-pass, exit 1 on any failure (prints every failure before exiting).
    PowerShell 5.1 compatible. ASCII only.
#>

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# See install.ps1 for why this is explicit rather than relying on module auto-load: a
# machine-wide PowerShell 7 install can put its own (incompatible) Microsoft.PowerShell.Security
# earlier in $env:PSModulePath, which makes Get-Acl (used by Test-DirectoryTrusted below) fail
# to auto-load under powershell.exe.
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop

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

# --- Merge-PerfLogUsersAddedFlag (monotonic, legacy scalar) -----------------

Assert-Equal 'first install, added -> true' $true (Merge-PerfLogUsersAddedFlag -ExistingValue $null -AddedMembershipThisRun $true -AlreadyMemberThisRun $false)
Assert-Equal 'first install, already member -> false' $false (Merge-PerfLogUsersAddedFlag -ExistingValue $null -AddedMembershipThisRun $false -AlreadyMemberThisRun $true)
Assert-Equal 'reinstall, existing true, already member -> stays true (monotonic)' $true (Merge-PerfLogUsersAddedFlag -ExistingValue $true -AddedMembershipThisRun $false -AlreadyMemberThisRun $true)
Assert-Equal 'reinstall, existing true, added again -> stays true' $true (Merge-PerfLogUsersAddedFlag -ExistingValue $true -AddedMembershipThisRun $true -AlreadyMemberThisRun $false)
Assert-Equal 'reinstall, existing false, already member -> stays false (present key untouched)' $false (Merge-PerfLogUsersAddedFlag -ExistingValue $false -AddedMembershipThisRun $false -AlreadyMemberThisRun $true)
Assert-Equal 'reinstall, FPS not offered this run, existing true -> untouched' $true (Merge-PerfLogUsersAddedFlag -ExistingValue $true -AddedMembershipThisRun $false -AlreadyMemberThisRun $false)
Assert-Equal 'reinstall, FPS not offered this run, existing false -> untouched' $false (Merge-PerfLogUsersAddedFlag -ExistingValue $false -AddedMembershipThisRun $false -AlreadyMemberThisRun $false)
Assert-Equal 'reinstall, FPS not offered, existing absent -> false' $false (Merge-PerfLogUsersAddedFlag -ExistingValue $null -AddedMembershipThisRun $false -AlreadyMemberThisRun $false)

# --- Resolve-PerfLogUsersAddedSids (monotonic union + legacy migration) -----

$sids1 = Resolve-PerfLogUsersAddedSids -ExistingSids $null -AddedSidThisRun 'S-1-5-21-1-2-3-1001' -LegacyAddedByApp $null -LegacySid $null
Assert-Equal 'first add: single SID in result' 1 (@($sids1).Count)
Assert-Equal 'first add: SID matches (round trip as array, not unrolled)' 'S-1-5-21-1-2-3-1001' (@($sids1)[0])

$sids2 = Resolve-PerfLogUsersAddedSids -ExistingSids @('S-1-5-21-1-2-3-1001') -AddedSidThisRun 'S-1-5-21-1-2-3-1002' -LegacyAddedByApp $null -LegacySid $null
Assert-Equal 'second add: union grows to 2' 2 (@($sids2).Count)
Assert-True 'second add: original SID retained' (@($sids2) -contains 'S-1-5-21-1-2-3-1001')
Assert-True 'second add: new SID present' (@($sids2) -contains 'S-1-5-21-1-2-3-1002')

$sids3 = Resolve-PerfLogUsersAddedSids -ExistingSids @('S-1-5-21-1-2-3-1001') -AddedSidThisRun 'S-1-5-21-1-2-3-1001' -LegacyAddedByApp $null -LegacySid $null
Assert-Equal 're-adding the same SID does not duplicate' 1 (@($sids3).Count)

$sids4 = Resolve-PerfLogUsersAddedSids -ExistingSids @('S-1-5-21-1-2-3-1001') -AddedSidThisRun $null -LegacyAddedByApp $null -LegacySid $null
Assert-Equal 'no add this run: existing list untouched' 1 (@($sids4).Count)

$sids5 = Resolve-PerfLogUsersAddedSids -ExistingSids $null -AddedSidThisRun $null -LegacyAddedByApp $true -LegacySid 'S-1-5-21-9-9-9-1'
Assert-Equal 'migration: legacy true+SID seeds the list when no list exists yet' 1 (@($sids5).Count)
Assert-Equal 'migration: seeded SID matches legacy SID' 'S-1-5-21-9-9-9-1' (@($sids5)[0])

$sids6 = Resolve-PerfLogUsersAddedSids -ExistingSids $null -AddedSidThisRun $null -LegacyAddedByApp $false -LegacySid 'S-1-5-21-9-9-9-1'
Assert-Equal 'migration: legacy false does not seed' 0 (@($sids6).Count)

$sids7 = Resolve-PerfLogUsersAddedSids -ExistingSids @() -AddedSidThisRun $null -LegacyAddedByApp $true -LegacySid 'S-1-5-21-9-9-9-1'
Assert-Equal 'migration: an already-present (even empty) list is never re-seeded from legacy' 0 (@($sids7).Count)

$sids8 = Resolve-PerfLogUsersAddedSids -ExistingSids $null -AddedSidThisRun $null -LegacyAddedByApp $null -LegacySid $null
Assert-Equal 'no state at all -> empty array, not null' 0 (@($sids8).Count)
Assert-True 'empty result is still an array (not unrolled to null/scalar)' ($null -ne $sids8)

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

# --- Single-SID list survives a real ConvertTo-Json/ConvertFrom-Json round trip -------------
# (PS 5.1 trap: a bare single-element array can unroll to a scalar across some boundaries; this
# proves the actual serialization path install.ps1/uninstall.ps1 use keeps it an array of 1.)

$singleSidState = @{ perfLogUsersAddedSids = @('S-1-5-21-1-2-3-1001') } | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$roundTrippedSids = @($singleSidState.perfLogUsersAddedSids | ForEach-Object { [string]$_ })
Assert-Equal 'single-SID list round-trips through JSON as an array of 1, not a bare scalar' 1 (@($roundTrippedSids).Count)
Assert-Equal 'round-tripped single SID value is intact' 'S-1-5-21-1-2-3-1001' (@($roundTrippedSids)[0])

# --- Test-AclHasDisallowedWrite (SID-based) ----------------------------------

$sidSystem = 'S-1-5-18'
$sidAdmins = 'S-1-5-32-544'
$sidUsers = 'S-1-5-32-545'
$sidEveryone = 'S-1-1-0'

$fullControl = [int][System.Security.AccessControl.FileSystemRights]::FullControl
$readExecute = [int][System.Security.AccessControl.FileSystemRights]::ReadAndExecute
$modify = [int][System.Security.AccessControl.FileSystemRights]::Modify

$safeAcl = @(
    [pscustomobject]@{ IdentitySid = $sidSystem; AccessControlType = 'Allow'; FileSystemRights = $fullControl }
    [pscustomobject]@{ IdentitySid = $sidAdmins; AccessControlType = 'Allow'; FileSystemRights = $fullControl }
    [pscustomobject]@{ IdentitySid = $sidUsers; AccessControlType = 'Allow'; FileSystemRights = $readExecute }
)
Assert-True 'safe ACL (SYSTEM+Administrators FullControl, Users ReadAndExecute) has no disallowed write' (-not (Test-AclHasDisallowedWrite -AccessRules $safeAcl))

$dangerousAcl = $safeAcl + @([pscustomobject]@{ IdentitySid = $sidUsers; AccessControlType = 'Allow'; FileSystemRights = $modify })
Assert-True 'ACL granting Users Modify is flagged as disallowed write' (Test-AclHasDisallowedWrite -AccessRules $dangerousAcl)

$everyoneAcl = @([pscustomobject]@{ IdentitySid = $sidEveryone; AccessControlType = 'Allow'; FileSystemRights = $fullControl })
Assert-True 'Everyone FullControl is flagged as disallowed write' (Test-AclHasDisallowedWrite -AccessRules $everyoneAcl)

$denyAcl = @([pscustomobject]@{ IdentitySid = $sidUsers; AccessControlType = 'Deny'; FileSystemRights = $fullControl })
Assert-True 'a Deny ACE never counts as a disallowed write' (-not (Test-AclHasDisallowedWrite -AccessRules $denyAcl))

Assert-True 'empty ACL has no disallowed write' (-not (Test-AclHasDisallowedWrite -AccessRules @()))

$nameSpoofAcl = @([pscustomobject]@{ IdentitySid = 'BUILTIN\Administrators'; AccessControlType = 'Allow'; FileSystemRights = $fullControl })
Assert-True 'a display NAME (not a SID) in IdentitySid is never treated as an allowed principal' (Test-AclHasDisallowedWrite -AccessRules $nameSpoofAcl)

# --- Test-AclOwnerIsAdmin (SID-based) ----------------------------------------

Assert-True 'SYSTEM SID owner is admin' (Test-AclOwnerIsAdmin -OwnerSid $sidSystem)
Assert-True 'Administrators SID owner is admin' (Test-AclOwnerIsAdmin -OwnerSid $sidAdmins)
Assert-True 'invoking-user SID owner is not admin' (-not (Test-AclOwnerIsAdmin -OwnerSid 'S-1-5-21-1-2-3-1001'))
Assert-True 'a display NAME string is never treated as an admin owner' (-not (Test-AclOwnerIsAdmin -OwnerSid 'BUILTIN\Administrators'))

# --- Test-AclIsTrustedRoot (owner + protection + ACL, combined) --------------

Assert-True 'trusted root: admin owner, protected, safe ACL' (Test-AclIsTrustedRoot -OwnerSid $sidAdmins -IsProtected $true -AccessRules $safeAcl)
Assert-True 'untrusted root: not protection-locked (inherits)' (-not (Test-AclIsTrustedRoot -OwnerSid $sidAdmins -IsProtected $false -AccessRules $safeAcl))
Assert-True 'untrusted root: non-admin owner' (-not (Test-AclIsTrustedRoot -OwnerSid 'S-1-5-21-1-2-3-1001' -IsProtected $true -AccessRules $safeAcl))
Assert-True 'untrusted root: disallowed write ACE' (-not (Test-AclIsTrustedRoot -OwnerSid $sidAdmins -IsProtected $true -AccessRules $dangerousAcl))

# --- Remove-ItemReparseSafe (real temp-dir junctions; admin-free in PS 5.1) --

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("hltest-$([Guid]::NewGuid().ToString('N'))")
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

try {
    # --- Test-DirectoryTrusted negative cases (real temp dirs; no admin rights needed for a
    # plain dir or a junction -- only New-ProtectedDirectoryAtomic's owner=Administrators write
    # needs elevation, which is why that one is exercised via a real-machine probe instead). ---

    $plainUntrustedDir = Join-Path $testRoot 'untrusted-plain'
    New-Item -ItemType Directory -Path $plainUntrustedDir -Force | Out-Null
    Assert-True 'a plain (non-protected, non-admin-owned) directory is never trusted' (-not (Test-DirectoryTrusted -Path $plainUntrustedDir))

    $junctionTargetForTrust = Join-Path $testRoot 'trust-junction-target'
    New-Item -ItemType Directory -Path $junctionTargetForTrust -Force | Out-Null
    $untrustedJunction = Join-Path $testRoot 'untrusted-junction'
    New-Item -ItemType Junction -Path $untrustedJunction -Target $junctionTargetForTrust | Out-Null
    Assert-True 'a reparse point (junction) is never trusted, regardless of its ACL' (-not (Test-DirectoryTrusted -Path $untrustedJunction))

    # Case 1: a plain tree deletes fully.
    $plainTree = Join-Path $testRoot 'plain'
    New-Item -ItemType Directory -Path (Join-Path $plainTree 'sub') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $plainTree 'file.txt') -Value 'hi'
    Set-Content -LiteralPath (Join-Path $plainTree 'sub\nested.txt') -Value 'hi'
    Remove-ItemReparseSafe -Path $plainTree
    Assert-True 'plain tree fully removed' (-not (Test-Path -LiteralPath $plainTree))

    # Case 2: a junction INSIDE the tree is deleted as a link; its target survives untouched.
    $targetDir = Join-Path $testRoot 'junction-target'
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $targetDir 'keepme.txt') -Value 'do not delete me'

    $treeWithJunction = Join-Path $testRoot 'with-junction'
    New-Item -ItemType Directory -Path $treeWithJunction -Force | Out-Null
    $junctionPath = Join-Path $treeWithJunction 'link'
    New-Item -ItemType Junction -Path $junctionPath -Target $targetDir | Out-Null

    Remove-ItemReparseSafe -Path $treeWithJunction
    Assert-True 'tree containing a junction is removed' (-not (Test-Path -LiteralPath $treeWithJunction))
    Assert-True 'junction target directory itself survives (never followed/deleted)' (Test-Path -LiteralPath $targetDir)
    Assert-True 'junction target contents survive untouched' (Test-Path -LiteralPath (Join-Path $targetDir 'keepme.txt'))

    # Case 3: the root itself being a reparse point is refused outright.
    $rootJunction = Join-Path $testRoot 'root-junction'
    New-Item -ItemType Junction -Path $rootJunction -Target $targetDir | Out-Null
    $threw = $false
    try {
        Remove-ItemReparseSafe -Path $rootJunction
    }
    catch {
        $threw = $true
    }
    Assert-True 'reparse-point root is refused (throws), never silently deleted or followed' $threw
    Assert-True 'refused root-junction link itself still exists' (Test-Path -LiteralPath $rootJunction)
}
finally {
    # Best-effort cleanup: remove the junction link objects first so a normal recursive delete
    # of $testRoot never follows them either.
    Get-ChildItem -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Attributes -band [System.IO.FileAttributes]::ReparsePoint } |
        ForEach-Object { [System.IO.Directory]::Delete($_.FullName, $false) }
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

# --- Summary -----------------------------------------------------------------

Write-Host ("test-installlib.ps1: {0} passed, {1} failed" -f $script:passCount, $script:failures.Count)

if ($script:failures.Count -gt 0) {
    foreach ($failure in $script:failures) {
        Write-Host $failure
    }

    exit 1
}

exit 0
