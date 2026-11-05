# Access to a restricted account's own registry hive (its HKCU), whether it is signed in or not.

function Test-UcIsAdmin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Members of the local Administrators group by SID (the group name differs between Windows languages).
function Get-UcAdminMembers { @(Get-LocalGroupMember -SID 'S-1-5-32-544') }

# The account to restrict: SID and profile folder. Refuses administrators.
function Get-UcAccount([string]$User) {
    $account = Get-LocalUser -Name $User -ErrorAction Stop
    $sid = $account.SID.Value
    if (Get-UcAdminMembers | Where-Object { $_.SID.Value -eq $sid }) {
        throw "$User is an administrator; restrictions are meant for standard accounts."
    }
    $profilePath = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid" -ErrorAction SilentlyContinue).ProfileImagePath
    if (-not $profilePath) { throw "The profile of $User does not exist yet: sign in as $User once, then sign out." }
    [pscustomobject]@{ User = $User; Sid = $sid; ProfilePath = $profilePath }
}

# Runs $Action with the registry path of the user's hive ("Registry::HKEY_USERS\<sid>").
# Loads NTUSER.DAT when the user is signed out and unloads it afterwards.
function Use-UcUserHive($Account, [scriptblock]$Action) {
    $root = "Registry::HKEY_USERS\$($Account.Sid)"
    $loadedHere = $false
    if (-not (Test-Path $root)) {
        $hive = Join-Path $Account.ProfilePath 'NTUSER.DAT'
        if (-not (Test-Path -LiteralPath $hive -PathType Leaf)) { throw "Profile hive not found: $hive" }
        & reg.exe load "HKU\$($Account.Sid)" $hive 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Cannot load $hive." }
        $loadedHere = $true
    }
    try {
        & $Action $root ($Account.Sid) (-not $loadedHere)
    } finally {
        if ($loadedHere) {
            [GC]::Collect()
            [GC]::WaitForPendingFinalizers()
            Start-Sleep -Milliseconds 300
            & reg.exe unload "HKU\$($Account.Sid)" 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Warning "Could not unload the hive of $($Account.User); it unloads at the next restart." }
        }
    }
}

# Create a registry key only if it is missing. Never use "New-Item -Force" on an existing registry
# key: it deletes the key with all its values and subkeys and creates an empty one.
function Confirm-Key([string]$Path) {
    if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }
}

# Lock: owned by Administrators, the user can only read. Unlock: only inherited permissions again
# (the user gets full control from HKCU\Software); the owner stays Administrators, because giving
# ownership to another account needs a privilege that is not enabled.
function Set-UcKeyAcl([string]$Path, [string]$UserSid, [bool]$Lock) {
    if (-not (Test-Path $Path)) { return }
    $acl = New-Object Security.AccessControl.RegistrySecurity
    if ($Lock) {
        $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
        $acl.SetAccessRuleProtection($true, $false)
        $inherit = [Security.AccessControl.InheritanceFlags]::ContainerInherit
        $none = [Security.AccessControl.PropagationFlags]::None
        foreach ($r in @(@('S-1-5-32-544', 'FullControl'), @('S-1-5-18', 'FullControl'), @($UserSid, 'ReadKey'))) {
            $who = New-Object Security.Principal.SecurityIdentifier($r[0])
            $acl.AddAccessRule((New-Object Security.AccessControl.RegistryAccessRule($who, $r[1], $inherit, $none, 'Allow')))
        }
    } else {
        $acl.SetAccessRuleProtection($false, $false)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

# True when the key is owned by Administrators and the user cannot write to it.
function Test-UcKeyLocked([string]$Path, [string]$UserSid) {
    if (-not (Test-Path $Path)) { return $false }
    # Not Get-Acl: in Windows PowerShell 5.1 it fails on "Registry::HKEY_USERS\..." paths ("Cannot find path").
    $acl = (Get-Item -LiteralPath $Path).GetAccessControl()
    $owner = (New-Object Security.Principal.NTAccount($acl.Owner)).Translate([Security.Principal.SecurityIdentifier]).Value
    if ($owner -ne 'S-1-5-32-544') { return $false }
    $write = [Security.AccessControl.RegistryRights]'SetValue, CreateSubKey, Delete, ChangePermissions, TakeOwnership, WriteKey'
    foreach ($a in $acl.Access) {
        if ($a.AccessControlType -ne 'Allow') { continue }
        $sid = try { $a.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { $a.IdentityReference.Value }
        if ($sid -in @($UserSid, 'S-1-5-11', 'S-1-1-0', 'S-1-5-32-545', 'S-1-5-4') -and (([int]$a.RegistryRights -band [int]$write) -ne 0)) { return $false }
    }
    $true
}
