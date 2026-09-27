Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# HardwareLive.InstallLib.psm1
#
# Pure, admin-free logic shared by install.ps1 / uninstall.ps1, split out so it can be unit
# tested (tools/test-installlib.ps1) without administrator rights, a real registry, or a real
# filesystem ACL. Functions here take their inputs as parameters (a fake registry map, a list
# of fake ACE objects, ...) instead of reaching out to the machine themselves; the thin
# wrappers that *do* touch the real registry/ACL live at the bottom and are excluded from the
# unit-testable surface on purpose.
#
# PowerShell 5.1 compatible: no ?:, ??, ?., no pwsh-only cmdlets. ASCII only.

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
# Profile path resolution (SID -> %LOCALAPPDATA% owner, for writing config.json as the target
# user rather than the elevated admin account that ran install.ps1).
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
# perfLogUsersAddedByApp monotonic merge (docs/SPEC.md Component 9)
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
# ACL verification predicate (Program Files / ProgramData\logs protected-ACL check)
# ---------------------------------------------------------------------------

# $AccessRules is an array of objects with .IdentityReference (string), .AccessControlType
# ('Allow'/'Deny') and .FileSystemRights (an int or [System.Security.AccessControl.FileSystemRights]),
# standing in for a real FileSystemSecurity.Access collection. Returns $true if any principal
# other than the allow-listed admin principals has an Allow ACE granting any write-capable
# right -- the privilege-escalation guard the spec requires before trusting an elevated-SYSTEM
# task to run the copied exe.
function Test-AclHasDisallowedWrite {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$AccessRules,

        [string[]]$AllowedFullControlPrincipals = @('NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators')
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
        foreach ($allowed in $AllowedFullControlPrincipals) {
            if ([string]::Equals([string]$rule.IdentityReference, $allowed, [System.StringComparison]::OrdinalIgnoreCase)) {
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
# not sufficient: the owner itself must also be an admin principal.
function Test-AclOwnerIsAdmin {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Owner,

        [string[]]$AllowedOwners = @('NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators')
    )

    foreach ($allowed in $AllowedOwners) {
        if ([string]::Equals($Owner, $allowed, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

# ---------------------------------------------------------------------------
# Real-machine wrappers (not exercised by the admin-free unit tests; install.ps1 uses these)
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

Export-ModuleMember -Function `
    Test-TargetUserSid, `
    Resolve-ProfilePathFromSid, `
    Resolve-LocalAppDataFromProfilePath, `
    Merge-PerfLogUsersAddedFlag, `
    Merge-JsonObject, `
    Test-AclHasDisallowedWrite, `
    Test-AclOwnerIsAdmin, `
    Get-ProfileImagePathFromRegistry
