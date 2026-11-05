# Looks at this PC: who can write where, which drives exist, where games are installed. Read-only.

# Everyone, Users, Authenticated Users, INTERACTIVE
$script:UcWeakPrincipals = @('S-1-1-0', 'S-1-5-32-545', 'S-1-5-11', 'S-1-5-4')

# Folders inside Windows that ordinary users can write to by default.
$script:UcSystemWritableFolders = @(
    'Temp', 'Tasks', 'tracing', 'Registration\CRMLog', 'System32\Tasks', 'SysWOW64\Tasks',
    'System32\spool\drivers\color', 'System32\spool\PRINTERS', 'System32\com\dmp', 'SysWOW64\com\dmp',
    'System32\FxsTmp', 'SysWOW64\FxsTmp', 'System32\Microsoft\Crypto\RSA\MachineKeys', 'debug\WIA', 'PLA\Reports',
    'PLA\Rules', 'PLA\Templates', 'servicing\Sessions'
)

function Get-UcWeakSids([string[]]$ExtraSids = @()) { @($script:UcWeakPrincipals) + @($ExtraSids | Where-Object { $_ }) }

function ConvertTo-UcSid($IdentityReference) {
    try { $IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { $IdentityReference.Value }
}

# Names of the given principals that may create or change files in $Path.
function Get-UcWritableBy([string]$Path, [string[]]$Sids) {
    $R = [Security.AccessControl.FileSystemRights]
    $mask = [int]($R::WriteData -bor $R::AppendData -bor $R::Delete -bor $R::ChangePermissions -bor $R::TakeOwnership)
    try { $acl = Get-Acl -LiteralPath $Path } catch { return @() }
    @($acl.Access | Where-Object {
        $_.AccessControlType -eq 'Allow' -and (([int]$_.FileSystemRights -band $mask) -ne 0) -and
        ((ConvertTo-UcSid $_.IdentityReference) -in $Sids)
    } | ForEach-Object { $_.IdentityReference.Value } | Select-Object -Unique)
}

function Test-UcPathCovered([string]$Path, [string[]]$Blocked) {
    foreach ($b in $Blocked) { if (Test-UcPathUnder $Path $b) { return $true } }
    $false
}

function Get-UcFixedDrives { @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object { "$($_.DeviceID)\" }) }

# Windows and Program Files: allowed even when the system drive root is blocked.
function Get-UcSystemAllowedPaths { @($env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramW6432) | Where-Object { $_ } | Select-Object -Unique }

# Places on this PC where the family could put a program and run it, by the "auto" switches in the config.
function Get-UcAutoPaths($Config) {
    $auto = $Config.auto
    $weak = Get-UcWeakSids
    $fixed = Get-UcFixedDrives
    $sysDrive = "$env:SystemDrive\"
    $list = New-Object System.Collections.Generic.List[object]
    function Add-Auto([string]$p, [string]$why) { $list.Add([pscustomobject]@{ Path = $p; Category = 'writable'; Why = $why }) }

    # Ordinary users can create folders in the root of the system drive by default (C:\anything).
    if ($auto.systemDriveRoot) { Add-Auto $sysDrive 'system drive root (users can create folders there)' }
    if ($auto.systemWritableFolders) {
        foreach ($f in $script:UcSystemWritableFolders) { Add-Auto (Join-Path $env:SystemRoot $f) 'writable folder inside Windows' }
        Add-Auto (Join-Path $env:SystemDrive 'Users\Public') 'shared by all users'
        Add-Auto $env:ProgramData 'users can create folders there'
    }
    $blockedRoots = @()
    if ($auto.writableDriveRoots) {
        foreach ($d in $fixed | Where-Object { $_ -ne $sysDrive }) {
            if (Get-UcWritableBy $d $weak) { Add-Auto $d 'drive writable by ordinary users'; $blockedRoots += $d }
        }
    }
    if ($auto.otherDriveLetters) {
        $used = $fixed | ForEach-Object { $_.Substring(0, 1) }
        foreach ($c in [char[]](65..90)) { if ("$c" -notin $used) { Add-Auto "${c}:\" 'USB, network or future drive' } }
    }
    if ($auto.recycleBins) { foreach ($d in $fixed) { Add-Auto "$d`$Recycle.Bin" 'Recycle Bin' } }

    # Program folders that ordinary users can change (one and two levels deep on other drives).
    if ($auto.writableProgramFolders) {
        $candidates = @()
        foreach ($pf in Get-UcSystemAllowedPaths | Where-Object { $_ -ne $env:SystemRoot }) {
            $candidates += Get-ChildItem -LiteralPath $pf -Directory -Force -ErrorAction SilentlyContinue
        }
        foreach ($d in $fixed | Where-Object { $_ -ne $sysDrive -and $_ -notin $blockedRoots }) {
            $l1 = @(Get-ChildItem -LiteralPath $d -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch '^\$RECYCLE\.BIN$|^System Volume Information$|^\$' })
            $candidates += $l1
            $candidates += $l1 | ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory -Force -ErrorAction SilentlyContinue }
        }
        foreach ($c in $candidates) {
            $who = Get-UcWritableBy $c.FullName $weak
            if ($who) { Add-Auto $c.FullName "program folder writable by $($who -join ', ')" }
        }
    }
    $list.ToArray()
}

# Game folders found on this PC that are not blocked yet (Steam libraries, Epic games, known launchers).
function Find-UcGameFolders($Config) {
    $found = New-Object System.Collections.Generic.List[string]
    $steamRoots = @()
    foreach ($k in 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        $p = (Get-ItemProperty $k -ErrorAction SilentlyContinue).InstallPath
        if ($p) { $steamRoots += $p }
    }
    foreach ($r in $steamRoots | Select-Object -Unique) {
        $found.Add($r)
        $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $found.Add(($m.Groups[1].Value -replace '\\\\', '\')) }
        }
    }
    $epic = Join-Path $env:ProgramData 'Epic\EpicGamesLauncher\Data\Manifests'
    foreach ($item in Get-ChildItem $epic -Filter *.item -ErrorAction SilentlyContinue) {
        try { $loc = (Get-Content $item.FullName -Raw | ConvertFrom-Json).InstallLocation; if ($loc) { $found.Add($loc) } } catch { }
    }
    foreach ($pf in Get-UcSystemAllowedPaths | Where-Object { $_ -ne $env:SystemRoot }) {
        foreach ($n in 'Steam', 'Epic Games', 'Riot Games', 'Battle.net', 'GOG Galaxy', 'EA Games', 'Electronic Arts', 'Ubisoft',
                       'Rockstar Games', 'Wargaming.net', 'Roblox', 'Minecraft Launcher', 'BlueStacks', 'BlueStacks_nxt') {
            $found.Add((Join-Path $pf $n))
        }
    }
    # Only folders not yet listed as games (a folder under a blocked drive root still needs a games
    # entry, otherwise "allow games" could not let it run).
    $games = @($Config.blockedPaths | Where-Object { $_.category -eq 'games' } | ForEach-Object { ConvertTo-UcPath $_.path })
    @($found | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | ForEach-Object { ConvertTo-UcPath $_ } | Select-Object -Unique |
      Where-Object { -not (Test-UcPathCovered $_ $games) })
}
