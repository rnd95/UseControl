# config.json: reading, saving, the edits the menu makes, and building the rule set.
# Nothing here touches the system, which is why these functions are covered by the Pester tests.

$script:UcCategories = @('writable', 'games', 'vpn', 'tools', 'other')
$script:UcBrowsers   = @('chrome', 'edge', 'firefox')
$script:UcInstallDir = Join-Path $env:ProgramFiles 'UseControl'
$script:UcDataDir    = Join-Path $env:ProgramData 'UseControl'

# Folders that must never be blocked as a whole: Windows and installed programs would stop working.
$script:UcProtectedPaths = @(
    "$env:SystemDrive\", $env:SystemRoot, "$env:SystemRoot\System32", "$env:SystemRoot\SysWOW64", "$env:SystemRoot\explorer.exe",
    $env:ProgramFiles, ${env:ProgramFiles(x86)}, "$env:ProgramFiles\Common Files", "${env:ProgramFiles(x86)}\Common Files"
) | Where-Object { $_ }

function Get-UcConfig([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Config not found: $Path" }
    $cfg = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
    foreach ($field in 'users', 'blockedPaths', 'blockedNames', 'browsers') {
        if ($null -eq $cfg.PSObject.Properties[$field]) { throw "Config $Path has no '$field'." }
    }
    # ConvertFrom-Json gives $null for some empty arrays; make every list a real array.
    $cfg.users = @($cfg.users | Where-Object { $_ })
    $cfg.blockedPaths = @($cfg.blockedPaths | Where-Object { $_ })
    $cfg.blockedNames = @($cfg.blockedNames | Where-Object { $_ })
    if ($null -eq $cfg.PSObject.Properties['allowedPaths']) { $cfg | Add-Member allowedPaths @() }
    $cfg.allowedPaths = @($cfg.allowedPaths | Where-Object { $_ })
    if ($null -eq $cfg.PSObject.Properties['auto']) { $cfg | Add-Member auto ([pscustomobject]@{}) }
    foreach ($a in 'systemDriveRoot', 'writableDriveRoots', 'otherDriveLetters', 'recycleBins', 'systemWritableFolders', 'writableProgramFolders') {
        if ($null -eq $cfg.auto.PSObject.Properties[$a]) { $cfg.auto | Add-Member $a $true }
    }
    if ($null -eq $cfg.PSObject.Properties['audit']) { $cfg | Add-Member audit ([pscustomobject]@{}) }
    foreach ($a in 'admins', 'privateFolders', 'hiddenAccounts', 'knownRemoteServices') {
        if ($null -eq $cfg.audit.PSObject.Properties[$a]) { $cfg.audit | Add-Member $a @() }
        $cfg.audit.$a = @($cfg.audit.$a | Where-Object { $_ })
    }
    foreach ($b in $script:UcBrowsers) {
        if ($cfg.browsers.$b) { $cfg.browsers.$b.extensionsAllowed = @($cfg.browsers.$b.extensionsAllowed | Where-Object { $_ }) }
    }
    $cfg
}

function Save-UcConfig($Config, [string]$Path) {
    $json = $Config | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText($Path, $json, (New-Object Text.UTF8Encoding($false)))
}

# "d:" -> "D:\", "C:\Foo\" -> "C:\Foo", quotes and spaces removed, machine-wide environment variables
# expanded (%ProgramFiles(x86)%\Steam). Only absolute local paths.
function ConvertTo-UcPath([string]$Path) {
    $p = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"').Trim())
    if ($p -notmatch '^[A-Za-z]:(\\|$)') { throw "Not an absolute path: '$Path'" }
    if ($p -match '[*?"<>|]') { throw "Invalid characters in path: '$Path'" }
    $p = $p.Substring(0, 1).ToUpper() + $p.Substring(1)
    if ($p.Length -le 3) { return $p.Substring(0, 2) + '\' }
    $p.TrimEnd('\')
}

# True when $Path is $Parent or inside it.
function Test-UcPathUnder([string]$Path, [string]$Parent) {
    $prefix = if ($Parent.EndsWith('\')) { $Parent } else { "$Parent\" }
    $Path -eq $Parent -or $Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

# Returns why a path cannot be blocked by hand, or $null if it can.
function Get-UcPathProblem([string]$Path) {
    $p = ConvertTo-UcPath $Path
    foreach ($prot in $script:UcProtectedPaths) {
        if ($p -eq $prot) { return "$p is needed by Windows or by every program." }
        if (Test-UcPathUnder $prot $p) { return "$p contains $prot, which Windows needs." }
    }
    $null
}

function Test-UcProgramName([string]$Name) { $Name -match '^[^\\/:*?"<>|]+\.(exe|com|msc|bat|cmd|scr|cpl)$' }
function Test-UcUserName([string]$Name) { $Name -match '^[^\\/"\[\]:|<>+=;,?*@]{1,20}$' }

function Assert-UcCategory([string]$Category) {
    if ($Category -notin $script:UcCategories) { throw "Unknown category '$Category'. Use one of: $($script:UcCategories -join ', ')." }
}

function Get-UcBlockedNameList($Config, [string[]]$Skip = @()) {
    @($Config.blockedNames | Where-Object { $_.category -notin $Skip } | ForEach-Object { $_.name })
}

# The Software Restriction Policy rules for one account.
#   AutoPaths      folders found on this PC (see Get-UcAutoPaths), objects with Path and Category
#   SystemAllowed  Windows and Program Files folders, which stay allowed under a blocked drive root
#   GamesAllowed   games time: game folders become allowed instead of blocked
# The most specific matching path wins in Windows, so a blocked subfolder of an allowed folder is
# blocked, and an allowed game folder inside a blocked drive root runs.
function Get-UcRuleSet($Config, [string]$ProfilePath, $AutoPaths = @(), [string[]]$SystemAllowed = @(), [bool]$GamesAllowed = $false) {
    $blocked = New-Object System.Collections.Generic.List[object]
    $allowed = New-Object System.Collections.Generic.List[object]
    if ($ProfilePath) { $blocked.Add([pscustomobject]@{ Path = (ConvertTo-UcPath $ProfilePath); Category = 'writable' }) }
    foreach ($a in $AutoPaths) { $blocked.Add([pscustomobject]@{ Path = (ConvertTo-UcPath $a.Path); Category = $a.Category }) }
    foreach ($e in $Config.blockedPaths) {
        $r = [pscustomobject]@{ Path = (ConvertTo-UcPath $e.path); Category = $e.category }
        if ($GamesAllowed -and $e.category -eq 'games') { $allowed.Add($r) } else { $blocked.Add($r) }
    }
    foreach ($s in $SystemAllowed) { $allowed.Add([pscustomobject]@{ Path = (ConvertTo-UcPath $s); Category = 'system' }) }
    foreach ($e in $Config.allowedPaths) { $allowed.Add([pscustomobject]@{ Path = (ConvertTo-UcPath $e.path); Category = 'allowed' }) }

    $gamePaths = @($allowed | Where-Object Category -eq 'games' | ForEach-Object Path)
    $outBlocked = @(); $seen = @{}
    foreach ($b in $blocked) {
        $k = $b.Path.ToLower()
        if ($seen.ContainsKey($k)) { continue }
        # During games time, anything inside an allowed game folder is not blocked.
        if (@($gamePaths | Where-Object { Test-UcPathUnder $b.Path $_ }).Count) { continue }
        $seen[$k] = $true; $outBlocked += $b
    }
    $outAllowed = @(); $seenA = @{}
    foreach ($a in $allowed) {
        $k = $a.Path.ToLower()
        if ($seenA.ContainsKey($k) -or $seen.ContainsKey($k)) { continue }   # the same path blocked: blocking wins
        $seenA[$k] = $true; $outAllowed += $a
    }
    [pscustomobject]@{ Blocked = $outBlocked; Allowed = $outAllowed }
}

function Add-UcBlockedPath($Config, [string]$Path, [string]$Category) {
    Assert-UcCategory $Category
    $p = ConvertTo-UcPath $Path
    $problem = Get-UcPathProblem $p
    if ($problem) { throw $problem }
    if ($Config.blockedPaths | Where-Object { (ConvertTo-UcPath $_.path) -eq $p }) { throw "$p is already blocked." }
    $Config.blockedPaths = @($Config.blockedPaths) + [pscustomobject]@{ path = $p; category = $Category }
    $Config
}

function Remove-UcBlockedPath($Config, [string]$Path) {
    $p = ConvertTo-UcPath $Path
    $kept = @($Config.blockedPaths | Where-Object { (ConvertTo-UcPath $_.path) -ne $p })
    if ($kept.Count -eq @($Config.blockedPaths).Count) { throw "$p is not in the blocked list." }
    $Config.blockedPaths = $kept
    $Config
}

# A program folder that should run although it is under a blocked place (for example C:\Tools or D:\Apps).
function Add-UcAllowedPath($Config, [string]$Path, [string]$Note = '') {
    $p = ConvertTo-UcPath $Path
    if ($p.Length -le 3) { throw "Allowing a whole drive ($p) would undo the protection." }
    if ($Config.allowedPaths | Where-Object { (ConvertTo-UcPath $_.path) -eq $p }) { throw "$p is already allowed." }
    $Config.allowedPaths = @($Config.allowedPaths) + [pscustomobject]@{ path = $p; note = $Note }
    $Config
}

function Remove-UcAllowedPath($Config, [string]$Path) {
    $p = ConvertTo-UcPath $Path
    $kept = @($Config.allowedPaths | Where-Object { (ConvertTo-UcPath $_.path) -ne $p })
    if ($kept.Count -eq @($Config.allowedPaths).Count) { throw "$p is not in the allowed list." }
    $Config.allowedPaths = $kept
    $Config
}

function Add-UcBlockedName($Config, [string]$Name, [string]$Category) {
    Assert-UcCategory $Category
    $n = $Name.Trim().Trim('"')
    if (-not (Test-UcProgramName $n)) { throw "'$Name' is not a program file name (for example game.exe)." }
    if ($Config.blockedNames | Where-Object { $_.name -eq $n }) { throw "$n is already blocked." }
    $Config.blockedNames = @($Config.blockedNames) + [pscustomobject]@{ name = $n; category = $Category }
    $Config
}

function Remove-UcBlockedName($Config, [string]$Name) {
    $n = $Name.Trim().Trim('"')
    $kept = @($Config.blockedNames | Where-Object { $_.name -ne $n })
    if ($kept.Count -eq @($Config.blockedNames).Count) { throw "$n is not in the list." }
    $Config.blockedNames = $kept
    $Config
}

function Add-UcUser($Config, [string]$User) {
    $u = $User.Trim()
    if (-not (Test-UcUserName $u)) { throw "'$User' is not a valid local account name." }
    if ($u -in $Config.users) { throw "$u is already in the list." }
    $Config.users = @($Config.users) + $u
    $Config
}

function Remove-UcUser($Config, [string]$User) {
    $kept = @($Config.users | Where-Object { $_ -ne $User.Trim() })
    if ($kept.Count -eq @($Config.users).Count) { throw "$User is not in the list." }
    $Config.users = $kept
    $Config
}

# Chrome/Edge IDs are 32 letters a-p; Firefox IDs are an e-mail-like string or a {GUID}.
function Test-UcExtensionId([string]$Browser, [string]$Id) {
    if ($Browser -eq 'firefox') { return $Id -match '^(\{[0-9a-fA-F-]{36}\}|[^\s@]+@[^\s@]+)$' }
    $Id -match '^[a-p]{32}$'
}

function Add-UcExtension($Config, [string]$Browser, [string]$Id) {
    if ($Browser -notin $script:UcBrowsers) { throw "Unknown browser '$Browser'." }
    $id = $Id.Trim()
    if (-not (Test-UcExtensionId $Browser $id)) { throw "'$Id' is not a valid $Browser extension ID." }
    $b = $Config.browsers.$Browser
    if ($id -in $b.extensionsAllowed) { throw "$id is already allowed in $Browser." }
    $b.extensionsAllowed = @($b.extensionsAllowed) + $id
    $Config
}

function Remove-UcExtension($Config, [string]$Browser, [string]$Id) {
    if ($Browser -notin $script:UcBrowsers) { throw "Unknown browser '$Browser'." }
    $b = $Config.browsers.$Browser
    $kept = @($b.extensionsAllowed | Where-Object { $_ -ne $Id.Trim() })
    if ($kept.Count -eq @($b.extensionsAllowed).Count) { throw "$Id is not in the $Browser list." }
    $b.extensionsAllowed = $kept
    $Config
}

# Registry type and data for a policy value from JSON.
function ConvertTo-UcRegValue($Value) {
    if ($Value -is [bool]) { return @{ Type = 'DWord'; Data = [int]$Value } }
    if ($Value -is [int] -or $Value -is [long]) { return @{ Type = 'DWord'; Data = [int]$Value } }
    @{ Type = 'String'; Data = [string]$Value }
}

function Get-UcFirefoxExtensionJson($Config) {
    $ext = [ordered]@{ '*' = [ordered]@{ installation_mode = 'blocked'; blocked_install_message = 'Ask the administrator to allow this extension.' } }
    foreach ($id in $Config.browsers.firefox.extensionsAllowed) { $ext[$id] = @{ installation_mode = 'allowed' } }
    $ext | ConvertTo-Json -Compress
}
