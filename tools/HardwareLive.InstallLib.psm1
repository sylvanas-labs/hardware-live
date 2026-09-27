Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# HardwareLive.InstallLib.psm1
#
# Pure, admin-free logic shared by install.ps1 / uninstall.ps1, split out so it can be unit
# tested (tools/test-installlib.ps1) without administrator rights, a real registry, or a real
# filesystem ACL. Functions here take their inputs as parameters (a fake registry map, a list
# of fake ACE objects, ...) instead of reaching out to the machine themselves; the thin
# wrappers that *do* touch the real registry/ACL/filesystem live at the bottom and are excluded
# from the unit-testable surface on purpose (except where noted -- junction creation doesn't
# need admin rights in PowerShell 5.1, so the reparse-safe delete wrapper IS unit tested).
#
# PowerShell 5.1 compatible: no ?:, ??, ?., no pwsh-only cmdlets. ASCII only.

# ---------------------------------------------------------------------------
# Well-known SIDs. Compare principals by SID everywhere in this module and its callers --
# never by localized/renamed display name (BUILTIN\Administrators is "Administrateurs" on a
# French box; a well-known SID never changes).
# ---------------------------------------------------------------------------

$script:SidSystem = 'S-1-5-18'
$script:SidAdministrators = 'S-1-5-32-544'
$script:SidUsers = 'S-1-5-32-545'

# ---------------------------------------------------------------------------
# SID validation
# ---------------------------------------------------------------------------

# Mirrors src/HardwareLive.Sampler/SamplerCommand.cs TryParseTargetUserSid exactly: only real
# user account SIDs are accepted - local/domain accounts (S-1-5-21-...) and Microsoft Entra ID
# (Azure AD) accounts (S-1-12-1-..., common on work PCs). If install.ps1 accepted a SID the
# sampler rejects (e.g. a well-known service SID), the Sampler task would be created
# successfully but hl-sampler.exe would exit silently at every logon -- so this must accept
# and reject exactly what the sampler does.
function Test-TargetUserSid {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Sid
    )

    if ([string]::IsNullOrWhiteSpace($Sid)) {
        return $false
    }

    try {
        $identifier = New-Object System.Security.Principal.SecurityIdentifier($Sid)
    }
    catch {
        return $false
    }

    $value = $identifier.Value
    return ($value.StartsWith('S-1-5-21-', [System.StringComparison]::OrdinalIgnoreCase) -or
        $value.StartsWith('S-1-12-1-', [System.StringComparison]::OrdinalIgnoreCase))
}

# ---------------------------------------------------------------------------
# Profile path resolution (SID -> %LOCALAPPDATA% owner). Used by uninstall.ps1 to resolve the
# *invoking* user's profile after elevation, and kept available for any elevated-context need;
# install.ps1's own FPS consent flow no longer needs it (that write now happens in the
# unelevated phase, where the process already IS that user).
# ---------------------------------------------------------------------------

# $ProfileMap is a hashtable of SID string -> ProfileImagePath string, standing in for the
# real ProfileList registry hive so this is testable without touching the registry.
function Resolve-ProfilePathFromSid {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Sid,

        [Parameter(Mandatory = $true)]
        [hashtable]$ProfileMap
    )

    if ($ProfileMap.ContainsKey($Sid)) {
        return $ProfileMap[$Sid]
    }

    return $null
}

function Resolve-LocalAppDataFromProfilePath {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProfilePath
    )

    return Join-Path -Path $ProfilePath -ChildPath 'AppData\Local'
}

# ---------------------------------------------------------------------------
# perfLogUsersAddedByApp monotonic merge (legacy scalar flag, docs/SPEC.md Component 9).
# Retained for backward compatibility / display and as the seed for the SID-list migration
# below; the SID list (Resolve-PerfLogUsersAddedSids) is now what uninstall actually acts on.
# ---------------------------------------------------------------------------

# $ExistingValue is $null (key absent), $true or $false, read from install-state.json.
# Exactly one of -AddedMembershipThisRun / -AlreadyMemberThisRun should be $true when the FPS
# consent flow ran at all; pass both $false when FPS wasn't offered/declined this run, which
# leaves the existing value untouched.
function Merge-PerfLogUsersAddedFlag {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [AllowNull()]
        [System.Nullable[bool]]$ExistingValue,

        [Parameter(Mandatory = $true)]
        [bool]$AddedMembershipThisRun,

        [Parameter(Mandatory = $true)]
        [bool]$AlreadyMemberThisRun
    )

    if ($AddedMembershipThisRun) {
        # We just added it: the flag is definitionally true, and this can never regress an
        # existing true to false (it only ever produces true here).
        return $true
    }

    if ($AlreadyMemberThisRun) {
        # Spec: "record perfLogUsersAddedByApp=false only if the key is absent." A present
        # value (true or false) is left exactly as recorded, since we didn't add it this run.
        if ($null -eq $ExistingValue) {
            return $false
        }

        return [bool]$ExistingValue
    }

    # FPS consent not exercised this run (declined or not offered): never touch the flag.
    if ($null -eq $ExistingValue) {
        return $false
    }

    return [bool]$ExistingValue
}

# ---------------------------------------------------------------------------
# perfLogUsersAddedSids monotonic union (docs/SPEC.md Component 9, hardened): uninstall must
# only ever remove a SID this app itself added, never overwrite-and-lose a previous run's SID
# (the old scalar perfLogUsersUserSid field was clobbered by every run, including runs that
# didn't add anything). Seeds itself once from the legacy scalar fields so an alpha.1 upgrade
# doesn't orphan a membership it already added.
# ---------------------------------------------------------------------------

function Resolve-PerfLogUsersAddedSids {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [AllowNull()]
        [string[]]$ExistingSids,

        [AllowNull()]
        [string]$AddedSidThisRun,

        [AllowNull()]
        [System.Nullable[bool]]$LegacyAddedByApp,

        [AllowNull()]
        [string]$LegacySid
    )

    $set = New-Object System.Collections.Generic.List[string]

    if ($null -ne $ExistingSids) {
        foreach ($sid in $ExistingSids) {
            if (-not [string]::IsNullOrWhiteSpace($sid) -and -not $set.Contains($sid)) {
                $set.Add($sid)
            }
        }
    }

    # One-time migration: only when this state has never had a SID list of its own (a fresh
    # upgrade from alpha.1), seed it from the legacy scalar flag+SID pair so a membership that
    # flag says "we added" is still tracked for removal.
    if ($null -eq $ExistingSids -and $LegacyAddedByApp -and -not [string]::IsNullOrWhiteSpace($LegacySid)) {
        if (-not $set.Contains($LegacySid)) {
            $set.Add($LegacySid)
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($AddedSidThisRun) -and -not $set.Contains($AddedSidThisRun)) {
        $set.Add($AddedSidThisRun)
    }

    # ,$array forces PowerShell to keep a single-element (or empty) array as an array instead
    # of unrolling it to a bare scalar / $null across the return boundary.
    return , $set.ToArray()
}

# ---------------------------------------------------------------------------
# config.json / install-state.json merge, preserving unknown keys
# ---------------------------------------------------------------------------

# $Existing is whatever ConvertFrom-Json produced ($null if the file was absent/empty/invalid);
# $Updates is a hashtable of keys to set/overwrite. Returns an ordered hashtable suitable for
# ConvertTo-Json. Unknown existing keys (anything not in $Updates) are preserved untouched.
function Merge-JsonObject {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [AllowNull()]
        $Existing,

        [Parameter(Mandatory = $true)]
        [hashtable]$Updates
    )

    $result = New-Object System.Collections.Specialized.OrderedDictionary

    if ($null -ne $Existing -and ($Existing -is [System.Management.Automation.PSCustomObject])) {
        foreach ($property in $Existing.PSObject.Properties) {
            $result[$property.Name] = $property.Value
        }
    }
    elseif ($null -ne $Existing -and ($Existing -is [hashtable])) {
        foreach ($key in $Existing.Keys) {
            $result[$key] = $Existing[$key]
        }
    }

    foreach ($key in $Updates.Keys) {
        $result[$key] = $Updates[$key]
    }

    return $result
}

# ---------------------------------------------------------------------------
# ACL verification predicates (SID-based). A display name (BUILTIN\Administrators) is
# localized and can be renamed; the well-known SID never changes, so every trust decision in
# this module and its callers compares SIDs, not names.
# ---------------------------------------------------------------------------

# $AccessRules is an array of objects with .IdentitySid (string SID), .AccessControlType
# ('Allow'/'Deny') and .FileSystemRights (an int or [FileSystemRights]), standing in for a real
# FileSystemSecurity.GetAccessRules($true, $true, [SecurityIdentifier]) collection. Returns
# $true if any principal other than the allow-listed admin SIDs has an Allow ACE granting any
# write-capable right -- the privilege-escalation guard the spec requires before trusting an
# elevated-SYSTEM task to run the copied exe.
function Test-AclHasDisallowedWrite {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$AccessRules,

        [string[]]$AllowedFullControlSids = @($script:SidSystem, $script:SidAdministrators)
    )

    # Deliberately built only from atomic write-capable rights -- NOT from the composite
    # Write/Modify/FullControl values, which also set read-bits (e.g. FullControl sets
    # ReadAndExecute's bits too), which would make a read-only ACE falsely overlap.
    $writeRights = [int]([System.Security.AccessControl.FileSystemRights]::WriteData -bor
        [System.Security.AccessControl.FileSystemRights]::AppendData -bor
        [System.Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [System.Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [System.Security.AccessControl.FileSystemRights]::Delete -bor
        [System.Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [System.Security.AccessControl.FileSystemRights]::TakeOwnership)

    foreach ($rule in $AccessRules) {
        if ($rule.AccessControlType -ne 'Allow') {
            continue
        }

        $isAllowedPrincipal = $false
        foreach ($allowed in $AllowedFullControlSids) {
            if ([string]::Equals([string]$rule.IdentitySid, $allowed, [System.StringComparison]::OrdinalIgnoreCase)) {
                $isAllowedPrincipal = $true
                break
            }
        }

        if ($isAllowedPrincipal) {
            continue
        }

        $rights = [int]$rule.FileSystemRights
        if (($rights -band $writeRights) -ne 0) {
            return $true
        }
    }

    return $false
}

# The object's *owner* has implicit WRITE_DAC regardless of its ACEs, so an ACE-only check is
# not sufficient: the owner itself must also be an admin principal (by SID).
function Test-AclOwnerIsAdmin {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$OwnerSid,

        [string[]]$AllowedOwnerSids = @($script:SidSystem, $script:SidAdministrators)
    )

    foreach ($allowed in $AllowedOwnerSids) {
        if ([string]::Equals($OwnerSid, $allowed, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

# Combines the two checks above plus the "is DACL protection actually on" bit into the single
# yes/no a caller needs before deciding whether to trust a pre-existing directory tree (item 2)
# or a pre-existing state file (item 5) rather than treat it as attacker-planted.
function Test-AclIsTrustedRoot {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$OwnerSid,

        [Parameter(Mandatory = $true)]
        [bool]$IsProtected,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$AccessRules
    )

    if (-not $IsProtected) {
        return $false
    }

    if (-not (Test-AclOwnerIsAdmin -OwnerSid $OwnerSid)) {
        return $false
    }

    if (Test-AclHasDisallowedWrite -AccessRules $AccessRules) {
        return $false
    }

    return $true
}

# ---------------------------------------------------------------------------
# Real-machine wrappers (not exercised by the admin-free unit tests, EXCEPT
# Remove-ItemReparseSafe: PowerShell 5.1 creates directory junctions without admin rights, so
# its reparse-refusal behavior is directly testable with real temp-directory junctions.)
# ---------------------------------------------------------------------------

function Get-ProfileImagePathFromRegistry {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Sid
    )

    $registryKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$Sid"
    if (-not (Test-Path -LiteralPath $registryKey)) {
        return $null
    }

    # ProfileImagePath is REG_EXPAND_SZ; Get-ItemProperty expands %SystemDrive%-style tokens,
    # a raw registry read would not.
    $item = Get-ItemProperty -LiteralPath $registryKey -Name 'ProfileImagePath' -ErrorAction Stop
    return $item.ProfileImagePath
}

# Recursively deletes $Path, refusing to descend into or follow reparse points (junctions,
# symlinks, mount points): each reparse point encountered is deleted as the link itself
# ([IO.Directory]::Delete($path, $false) never touches its target), never walked into. Refuses
# outright if $Path itself is a reparse point, since there is then no safe "delete the link,
# not the target" option available to the caller (the caller almost always wants the target
# tree gone, and this function must never silently do that through a planted link).
function Remove-ItemReparseSafe {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $root = Get-Item -LiteralPath $Path -Force
    if ($root.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw "$($root.FullName) is a reparse point (junction/symlink). Refusing to delete through it -- remove it manually and re-run."
    }

    if (-not $PSCmdlet.ShouldProcess($Path, 'remove (reparse-safe)')) {
        return
    }

    function Remove-Recurse([System.IO.FileSystemInfo]$item) {
        if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            # A link, whether to a file or a directory: delete the link object itself, never
            # its target's contents.
            if ($item.PSIsContainer) {
                [System.IO.Directory]::Delete($item.FullName, $false)
            }
            else {
                [System.IO.File]::Delete($item.FullName)
            }
            return
        }

        if ($item.PSIsContainer) {
            $children = Get-ChildItem -LiteralPath $item.FullName -Force -ErrorAction SilentlyContinue
            foreach ($child in $children) {
                Remove-Recurse -item $child
            }
            [System.IO.Directory]::Delete($item.FullName, $false)
        }
        else {
            if ($item.IsReadOnly) {
                $item.IsReadOnly = $false
            }
            [System.IO.File]::Delete($item.FullName)
        }
    }

    Remove-Recurse -item $root
}

# Atomically creates $Path with the given DirectorySecurity baked in at creation time (no
# create-then-Set-Acl window an attacker could race). Refuses if $Path already exists -- the
# caller must rename any pre-existing untrusted tree aside first -- and verifies the resulting
# ACL by SID before returning, throwing if verification fails for any reason (including the
# case where .NET silently no-ops because the path already existed underneath us).
function New-ProtectedDirectoryAtomic {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (Test-Path -LiteralPath $Path) {
        throw "$Path already exists; New-ProtectedDirectoryAtomic requires a path that does not exist (caller must rename any pre-existing tree aside first)."
    }

    if (-not $PSCmdlet.ShouldProcess($Path, 'create with protected ACL')) {
        return
    }

    $sidSystem = New-Object System.Security.Principal.SecurityIdentifier($script:SidSystem)
    $sidAdmins = New-Object System.Security.Principal.SecurityIdentifier($script:SidAdministrators)
    $sidUsers = New-Object System.Security.Principal.SecurityIdentifier($script:SidUsers)

    $security = New-Object System.Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($sidAdmins)

    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [System.Security.AccessControl.PropagationFlags]::None
    $security.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sidSystem, 'FullControl', $inherit, $propagation, 'Allow')))
    $security.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sidAdmins, 'FullControl', $inherit, $propagation, 'Allow')))
    $security.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sidUsers, 'ReadAndExecute', $inherit, $propagation, 'Allow')))

    $directoryInfo = New-Object System.IO.DirectoryInfo($Path)
    $directoryInfo.Create($security)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "New-ProtectedDirectoryAtomic: $Path does not exist after Create(security)."
    }

    $verifyAcl = Get-Acl -LiteralPath $Path
    $ownerSid = $verifyAcl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
    $accessRules = @()
    foreach ($rule in $verifyAcl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
        $accessRules += [pscustomobject]@{
            IdentitySid       = $rule.IdentityReference.Value
            AccessControlType = $rule.AccessControlType.ToString()
            FileSystemRights  = [int]$rule.FileSystemRights
        }
    }

    if (-not (Test-AclIsTrustedRoot -OwnerSid $ownerSid -IsProtected $verifyAcl.AreAccessRulesProtected -AccessRules $accessRules)) {
        throw "New-ProtectedDirectoryAtomic: $Path was created but its ACL does not verify as trusted (owner=$ownerSid, protected=$($verifyAcl.AreAccessRulesProtected))."
    }
}

# Reads the real ACL/reparse state of $Path (which must already exist) and returns $true only
# if it is trusted: not a reparse point, DACL protection on, admin-owned, no disallowed write.
function Test-DirectoryTrusted {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        return $false
    }

    $acl = Get-Acl -LiteralPath $Path
    $ownerSid = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
    $accessRules = @()
    foreach ($rule in $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
        $accessRules += [pscustomobject]@{
            IdentitySid       = $rule.IdentityReference.Value
            AccessControlType = $rule.AccessControlType.ToString()
            FileSystemRights  = [int]$rule.FileSystemRights
        }
    }

    return Test-AclIsTrustedRoot -OwnerSid $ownerSid -IsProtected $acl.AreAccessRulesProtected -AccessRules $accessRules
}

Export-ModuleMember -Function `
    Test-TargetUserSid, `
    Resolve-ProfilePathFromSid, `
    Resolve-LocalAppDataFromProfilePath, `
    Merge-PerfLogUsersAddedFlag, `
    Resolve-PerfLogUsersAddedSids, `
    Merge-JsonObject, `
    Test-AclHasDisallowedWrite, `
    Test-AclOwnerIsAdmin, `
    Test-AclIsTrustedRoot, `
    Get-ProfileImagePathFromRegistry, `
    Remove-ItemReparseSafe, `
    New-ProtectedDirectoryAtomic, `
    Test-DirectoryTrusted
