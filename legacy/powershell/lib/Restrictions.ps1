# Writes the restrictions into each restricted account's hive (Software Restriction Policy, blocked
# names, command prompt/registry editor off, browser policies) plus one machine-wide setting
# (no Store installs for standard users).

$script:UcPolicyKeys = @{
    Explorer    = 'Software\Microsoft\Windows\CurrentVersion\Policies\Explorer'
    System      = 'Software\Microsoft\Windows\CurrentVersion\Policies\System'
    WinSystem   = 'Software\Policies\Microsoft\Windows\System'
    DisallowRun = 'Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\DisallowRun'
    Safer       = 'Software\Policies\Microsoft\Windows\Safer'
}
# Made read-only for the user so the restrictions cannot be deleted from inside the account.
$script:UcLockedKeys = @('Software\Policies', 'Software\Microsoft\Windows\CurrentVersion\Policies')
$script:UcAppxKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Appx'

# File types the restriction policy checks. LNK and URL are left out on purpose: otherwise every
# shortcut on the Desktop and in the Start menu (they live in the profile) would be blocked.
$script:UcExecutableTypes = @('ADE','ADP','BAS','BAT','CHM','CMD','COM','CPL','CRT','EXE','HLP','HTA','INF','INS','ISP',
                              'JS','JSE','MDB','MDE','MSC','MSI','MSP','MST','OCX','PCD','PIF','PS1','REG','SCR','SHS',
                              'VB','VBE','VBS','WSC','WSF','WSH')

function Get-UcAccounts($Config) {
    if (-not $Config.users) { throw 'No accounts to restrict: add one (users in config.json, or Add user).' }
    @($Config.users | ForEach-Object { Get-UcAccount $_ })
}

# Applies the config to every listed account (or with -Undo removes everything).
function Invoke-UcApplyAll($Config, [switch]$Undo) {
    $gamesAllowed = Test-UcGamesAllowedNow
    $auto = if ($Undo) { @() } else { Get-UcAutoPaths $Config }
    $system = Get-UcSystemAllowedPaths
    $out = foreach ($acc in Get-UcAccounts $Config) {
        $rules = Get-UcRuleSet $Config $acc.ProfilePath $auto $system $gamesAllowed
        $names = Get-UcBlockedNameList $Config $(if ($gamesAllowed) { @('games') } else { @() })
        Set-UcAccountRestrictions $Config $acc $rules $names -Undo:$Undo
        [pscustomobject]@{ User = $acc.User; Blocked = @($rules.Blocked).Count; Allowed = @($rules.Allowed).Count; Names = @($names).Count }
    }

    # Machine-wide: standard users cannot install Store/MSIX apps (games come from the Store too).
    if ($Config.blockStoreInstalls -and -not $Undo) {
        Confirm-Key $script:UcAppxKey
        New-ItemProperty -Path $script:UcAppxKey -Name BlockNonAdminUserInstall -PropertyType DWord -Value 1 -Force | Out-Null
    } else {
        Remove-ItemProperty -Path $script:UcAppxKey -Name BlockNonAdminUserInstall -ErrorAction SilentlyContinue
    }
    $out
}

function Write-UcSaferRules([string]$Ci, [string]$Level, $Rules) {
    Confirm-Key "$Ci\$Level\Paths"
    foreach ($p in $Rules) {
        $rule = "$Ci\$Level\Paths\{$([guid]::NewGuid())}"
        Confirm-Key $rule
        New-ItemProperty -Path $rule -Name ItemData     -PropertyType String -Value $p.Path -Force | Out-Null
        New-ItemProperty -Path $rule -Name SaferFlags   -PropertyType DWord  -Value 0 -Force | Out-Null
        New-ItemProperty -Path $rule -Name Description  -PropertyType String -Value "UseControl:$($p.Category)" -Force | Out-Null
        New-ItemProperty -Path $rule -Name LastModified -PropertyType QWord  -Value ([DateTime]::Now.ToFileTime()) -Force | Out-Null
    }
}

function Set-UcAccountRestrictions($Config, $Account, $Rules, [string[]]$Names, [switch]$Undo) {
    Use-UcUserHive $Account {
        param($root, $sid, $signedIn)
        $k = $script:UcPolicyKeys

        # Settings that earlier versions wrote and that are no longer wanted.
        Remove-ItemProperty -Path "$root\$($k.Explorer)" -Name NoControlPanel -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path "$root\$($k.System)" -Name DisableTaskMgr -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path "$root\Software\Policies\Microsoft\WindowsStore" -Name RemoveWindowsStore -ErrorAction SilentlyContinue

        # Start clean: the lists are rebuilt from the config every time.
        foreach ($owned in @($k.Safer, $k.DisallowRun)) { Remove-Item -Path "$root\$owned" -Recurse -ErrorAction SilentlyContinue }
        Remove-UcBrowserPolicies $root

        if ($Undo) {
            foreach ($lk in $script:UcLockedKeys) { Set-UcKeyAcl "$root\$lk" $sid $false }
            Remove-ItemProperty -Path "$root\$($k.Explorer)" -Name NoDriveTypeAutoRun, DisallowRun -ErrorAction SilentlyContinue
            Remove-ItemProperty -Path "$root\$($k.System)" -Name DisableRegistryTools -ErrorAction SilentlyContinue
            Remove-ItemProperty -Path "$root\$($k.WinSystem)" -Name DisableCMD -ErrorAction SilentlyContinue
            return
        }

        Confirm-Key "$root\$($k.Explorer)"
        New-ItemProperty -Path "$root\$($k.Explorer)" -Name NoDriveTypeAutoRun -PropertyType DWord -Value 255 -Force | Out-Null
        New-ItemProperty -Path "$root\$($k.Explorer)" -Name DisallowRun -PropertyType DWord -Value 1 -Force | Out-Null
        Confirm-Key "$root\$($k.System)"
        New-ItemProperty -Path "$root\$($k.System)" -Name DisableRegistryTools -PropertyType DWord -Value 1 -Force | Out-Null
        Confirm-Key "$root\$($k.WinSystem)"
        New-ItemProperty -Path "$root\$($k.WinSystem)" -Name DisableCMD -PropertyType DWord -Value 1 -Force | Out-Null

        # Programs blocked by name (when started from Explorer, Start or Win+R)
        Confirm-Key "$root\$($k.DisallowRun)"
        $i = 1
        foreach ($n in $Names) { New-ItemProperty -Path "$root\$($k.DisallowRun)" -Name "$i" -PropertyType String -Value $n -Force | Out-Null; $i++ }

        # Software Restriction Policy: everything runs except the blocked folders; allowed folders
        # inside blocked ones run again (Windows uses the most specific matching path).
        $ci = "$root\$($k.Safer)\CodeIdentifiers"
        Confirm-Key $ci
        New-ItemProperty -Path $ci -Name DefaultLevel        -PropertyType DWord -Value 0x40000 -Force | Out-Null  # Unrestricted
        New-ItemProperty -Path $ci -Name TransparentEnabled  -PropertyType DWord -Value 1 -Force | Out-Null        # not DLLs
        New-ItemProperty -Path $ci -Name PolicyScope         -PropertyType DWord -Value 0 -Force | Out-Null
        New-ItemProperty -Path $ci -Name AuthenticodeEnabled -PropertyType DWord -Value 0 -Force | Out-Null
        New-ItemProperty -Path $ci -Name ExecutableTypes     -PropertyType MultiString -Value $script:UcExecutableTypes -Force | Out-Null
        Write-UcSaferRules $ci '0' $Rules.Blocked          # Disallowed
        Write-UcSaferRules $ci '262144' $Rules.Allowed     # Unrestricted

        Set-UcBrowserPolicies $root $Config

        foreach ($lk in $script:UcLockedKeys) { Confirm-Key "$root\$lk"; Set-UcKeyAcl "$root\$lk" $sid $true }
    }
}

# What is currently written in one account's hive.
function Get-UcRestrictionState($Account) {
    Use-UcUserHive $Account {
        param($root, $sid, $signedIn)
        $k = $script:UcPolicyKeys
        $rules = @(Get-ChildItem "$root\$($k.Safer)\CodeIdentifiers\0\Paths" -ErrorAction SilentlyContinue | ForEach-Object { Get-ItemProperty $_.PSPath })
        $allowed = @(Get-ChildItem "$root\$($k.Safer)\CodeIdentifiers\262144\Paths" -ErrorAction SilentlyContinue)
        $names = Get-ItemProperty "$root\$($k.DisallowRun)" -ErrorAction SilentlyContinue
        $nameCount = if ($names) { @($names.PSObject.Properties | Where-Object { $_.Name -match '^\d+$' }).Count } else { 0 }
        $byCat = @{}
        foreach ($r in $rules) { $c = ($r.Description -replace '^(UseControl|Family-PC):', ''); $byCat[$c] = 1 + [int]$byCat[$c] }
        [pscustomobject]@{
            User            = $Account.User
            SignedIn        = $signedIn
            Applied         = ($rules.Count -gt 0)
            BlockedRules    = $rules.Count
            AllowedRules    = $allowed.Count
            RulesByCategory = $byCat
            NameRules       = $nameCount
            GamesBlocked    = [bool]($byCat['games'])
            Locked          = (@($script:UcLockedKeys | Where-Object { -not (Test-UcKeyLocked "$root\$_" $sid) }).Count -eq 0)
        }
    }
}
