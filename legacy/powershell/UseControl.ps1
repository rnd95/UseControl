<#
.SYNOPSIS
  UseControl: restrictions for standard Windows accounts (games, VPNs, installing programs, browser
  extensions), games for a limited time and a security check. Console menu; every item also works
  as -Action, for scripts and remote tools (MeshCentral "Run commands", RMM, scheduled tasks).

.EXAMPLE
  .\UseControl.ps1                                    # menu
  .\UseControl.ps1 -Action AddUser -User kids
  .\UseControl.ps1 -Action Status
  .\UseControl.ps1 -Action AllowGames -Hours 2
  .\UseControl.ps1 -Action Block -Path "D:\Games" -Category games
  .\UseControl.ps1 -Action Block -Name game.exe -Category games
  .\UseControl.ps1 -Action AllowPath -Path "C:\Tools"
  .\UseControl.ps1 -Action DetectGames -Add
  .\UseControl.ps1 -Action AllowExtension -Browser chrome -ExtensionId <32 letters>
  .\UseControl.ps1 -Action Audit
#>
#Requires -RunAsAdministrator
param(
    [ValidateSet('Menu', 'Status', 'Preview', 'Apply', 'Undo', 'AllowGames', 'LockGames', 'GamesCheck', 'Block', 'Unblock',
                 'AllowPath', 'RemoveAllowPath', 'AddUser', 'RemoveUser', 'DetectGames', 'AllowExtension', 'RemoveExtension',
                 'Audit', 'Install')]
    [string]$Action = 'Menu',
    [double]$Hours = 1,
    [string]$Path,
    [string]$Name,
    [string]$User,
    [ValidateSet('writable', 'games', 'vpn', 'tools', 'other')]
    [string]$Category = 'other',
    [ValidateSet('chrome', 'edge', 'firefox')]
    [string]$Browser = 'chrome',
    [string]$ExtensionId,
    [switch]$Add,
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
foreach ($lib in 'Config', 'Scan', 'Hive', 'Browsers', 'Restrictions', 'Games', 'Audit', 'Status', 'Install') { . (Join-Path $PSScriptRoot "lib\$lib.ps1") }

if (-not $ConfigPath) { $ConfigPath = Resolve-UcConfigPath $PSScriptRoot }
$script:InstalledScript = Join-Path $script:UcInstallDir 'UseControl.ps1'
$script:Config = Get-UcConfig $ConfigPath

function Invoke-Apply {
    $r = Invoke-UcApplyAll $script:Config
    $msg = ($r | ForEach-Object { "$($_.User): $($_.Blocked) blocked / $($_.Allowed) allowed folders, $($_.Names) names" }) -join '; '
    if (Test-UcGamesAllowedNow) { $msg += " (games allowed until $(Get-UcGamesUntil))" }
    Write-UcLog "Applied: $msg"
    "Applied. $msg"
}

# Saves a config change and applies it right away.
function Save-AndApply($cfg) {
    Save-UcConfig $cfg $ConfigPath
    $script:Config = Get-UcConfig $ConfigPath
    if ($script:Config.users) { Invoke-Apply } else { 'Saved. No accounts listed yet, nothing applied.' }
}

function Show-Preview {
    $auto = Get-UcAutoPaths $script:Config
    $system = Get-UcSystemAllowedPaths
    foreach ($u in $script:Config.users) {
        $acc = Get-UcAccount $u
        $rules = Get-UcRuleSet $script:Config $acc.ProfilePath $auto $system (Test-UcGamesAllowedNow)
        "=== ${u}: programs may NOT start from ==="
        $rules.Blocked | Format-Table Path, Category -AutoSize | Out-String -Width 200
        "=== ${u}: allowed again inside those ==="
        $rules.Allowed | Format-Table Path, Category -AutoSize | Out-String -Width 200
    }
    "Found automatically on this PC:"
    $auto | Format-Table Path, Why -AutoSize | Out-String -Width 200
    "Blocked program names:"
    $script:Config.blockedNames | Group-Object category | ForEach-Object { "  {0}: {1}" -f $_.Name, (($_.Group | ForEach-Object name) -join ', ') }
    ""
    "Browser extensions allowed:"
    foreach ($b in 'chrome', 'edge', 'firefox') { "  {0}: {1}" -f $b, $(if ($script:Config.browsers.$b.extensionsAllowed) { $script:Config.browsers.$b.extensionsAllowed -join ', ' } else { '(none)' }) }
}

function Show-Audit {
    $rows = Invoke-UcAudit $script:Config
    foreach ($r in $rows) {
        $color = @{ OK = 'Green'; WARN = 'Yellow'; FAIL = 'Red' }[$r.Result]
        Write-Host ("[{0,-4}] {1}" -f $r.Result, $r.Check) -ForegroundColor $color
        Write-Host ("       {0}" -f $r.Detail)
    }
    $f = @($rows | Where-Object Result -eq 'FAIL').Count; $w = @($rows | Where-Object Result -eq 'WARN').Count
    Write-Host ("`n{0} checks: {1} FAIL, {2} WARN" -f $rows.Count, $f, $w) -ForegroundColor $(if ($f) { 'Red' } elseif ($w) { 'Yellow' } else { 'Green' })
}

function Invoke-UcAction([string]$A) {
    switch ($A) {
        'Status'   { Get-UcStatusText $script:Config }
        'Preview'  { Show-Preview }
        'Apply'    { Invoke-Apply }
        'Undo'     { Set-UcGamesUntil $null; Unregister-ScheduledTask -TaskName $script:UcTaskName -Confirm:$false -ErrorAction SilentlyContinue
                     Invoke-UcApplyAll $script:Config -Undo | Out-Null; Write-UcLog 'Restrictions removed'
                     "Restrictions removed for: $($script:Config.users -join ', ')." }
        'AllowGames' { $u = Grant-UcGames $script:Config $Hours $script:InstalledScript
                       "Games allowed until {0:yyyy-MM-dd HH:mm}. A signed-in account may need to sign out and back in." -f $u }
        'LockGames'  { Revoke-UcGames $script:Config; 'Games blocked again.' }
        'GamesCheck' { $r = Invoke-UcGamesCheck $script:Config; Write-UcLog "Games check: $r"; $r }
        'Block'    { if ($Path) { $c = Add-UcBlockedPath $script:Config $Path $Category } elseif ($Name) { $c = Add-UcBlockedName $script:Config $Name $Category } else { throw 'Give -Path or -Name.' }
                     Save-AndApply $c }
        'Unblock'  { if ($Path) { $c = Remove-UcBlockedPath $script:Config $Path } elseif ($Name) { $c = Remove-UcBlockedName $script:Config $Name } else { throw 'Give -Path or -Name.' }
                     Save-AndApply $c }
        'AllowPath'       { if (-not $Path) { throw 'Give -Path.' }; Save-AndApply (Add-UcAllowedPath $script:Config $Path) }
        'RemoveAllowPath' { if (-not $Path) { throw 'Give -Path.' }; Save-AndApply (Remove-UcAllowedPath $script:Config $Path) }
        'AddUser'  { if (-not $User) { throw 'Give -User.' }; Get-UcAccount $User | Out-Null; Save-AndApply (Add-UcUser $script:Config $User) }
        'RemoveUser' { if (-not $User) { throw 'Give -User.' }
                       $c = Remove-UcUser $script:Config $User
                       try { Set-UcAccountRestrictions $script:Config (Get-UcAccount $User) $null @() -Undo } catch { Write-Warning "Could not remove the restrictions of ${User}: $($_.Exception.Message)" }
                       Save-UcConfig $c $ConfigPath; $script:Config = Get-UcConfig $ConfigPath
                       "$User is no longer restricted." }
        'DetectGames' { $found = Find-UcGameFolders $script:Config
                        if (-not $found) { 'No new game folders found.' }
                        elseif ($Add) { $c = $script:Config; foreach ($p in $found) { $c = Add-UcBlockedPath $c $p 'games' }; "Added: $($found -join '; ')"; Save-AndApply $c }
                        else { "Found (add with -Add):"; $found } }
        'AllowExtension'  { if (-not $ExtensionId) { throw 'Give -ExtensionId.' }; Save-AndApply (Add-UcExtension $script:Config $Browser $ExtensionId) }
        'RemoveExtension' { if (-not $ExtensionId) { throw 'Give -ExtensionId.' }; Save-AndApply (Remove-UcExtension $script:Config $Browser $ExtensionId) }
        'Audit'    { Show-Audit }
        'Install'  { Install-Uc $PSScriptRoot | Format-List | Out-String
                     "From now on use the desktop shortcut 'UseControl' or $script:InstalledScript; lists edited there are kept there." }
    }
}

# --- console menu ------------------------------------------------------------------------------------
function Read-Choice([string]$Prompt, [string[]]$Allowed) {
    while ($true) {
        $v = (Read-Host $Prompt).Trim()
        if (-not $Allowed -or $v -in $Allowed) { return $v }
        Write-Host "Choose one of: $($Allowed -join ', ')" -ForegroundColor Yellow
    }
}

function Invoke-Safely([scriptblock]$Do) { try { & $Do | Out-Host } catch { Write-Host $_.Exception.Message -ForegroundColor Red } }

function Show-ProgramsMenu {
    while ($true) {
        Write-Host "`n--- Programs ---" -ForegroundColor Cyan
        Write-Host ' 1 Show what is blocked and allowed'
        Write-Host ' 2 Block a folder'
        Write-Host ' 3 Unblock a folder'
        Write-Host ' 4 Block a program name (game.exe)'
        Write-Host ' 5 Unblock a program name'
        Write-Host ' 6 Allow a program folder inside a blocked place'
        Write-Host ' 7 Remove an allowed folder'
        Write-Host ' 8 Detect game folders'
        Write-Host ' 0 Back'
        $c = Read-Choice 'Choice' @('0', '1', '2', '3', '4', '5', '6', '7', '8')
        if ($c -eq '0') { return }
        Invoke-Safely {
            switch ($c) {
                '1' { Show-Preview }
                '2' { $p = Read-Host 'Folder'; $cat = Read-Choice 'Category (games/vpn/other)' @('games', 'vpn', 'other'); Save-AndApply (Add-UcBlockedPath $script:Config $p $cat) }
                '3' { Save-AndApply (Remove-UcBlockedPath $script:Config (Read-Host 'Folder to unblock')) }
                '4' { $n = Read-Host 'Program file name'; $cat = Read-Choice 'Category (games/vpn/tools/other)' @('games', 'vpn', 'tools', 'other'); Save-AndApply (Add-UcBlockedName $script:Config $n $cat) }
                '5' { Save-AndApply (Remove-UcBlockedName $script:Config (Read-Host 'Program file name to unblock')) }
                '6' { Save-AndApply (Add-UcAllowedPath $script:Config (Read-Host 'Program folder to allow')) }
                '7' { Save-AndApply (Remove-UcAllowedPath $script:Config (Read-Host 'Allowed folder to remove')) }
                '8' { $found = Find-UcGameFolders $script:Config
                      if (-not $found) { 'No new game folders found.' }
                      else { $found | ForEach-Object { "  $_" } | Out-Host
                             if ((Read-Choice 'Block all of these as games? (y/n)' @('y', 'n')) -eq 'y') {
                                 $cfg = $script:Config; foreach ($p in $found) { $cfg = Add-UcBlockedPath $cfg $p 'games' }; Save-AndApply $cfg } } }
            }
        }
    }
}

function Show-ExtensionMenu {
    while ($true) {
        Write-Host "`n--- Browser extensions ---" -ForegroundColor Cyan
        foreach ($b in 'chrome', 'edge', 'firefox') { Write-Host ("  {0}: {1}" -f $b, $(if ($script:Config.browsers.$b.extensionsAllowed) { $script:Config.browsers.$b.extensionsAllowed -join ', ' } else { '(none)' })) }
        Write-Host ' 1 Allow an extension'
        Write-Host ' 2 Remove an allowed extension'
        Write-Host ' 0 Back'
        Write-Host '  Chrome/Edge ID: the 32 letters at the end of the extension page address in the store.' -ForegroundColor DarkGray
        $c = Read-Choice 'Choice' @('0', '1', '2')
        if ($c -eq '0') { return }
        Invoke-Safely {
            $b = Read-Choice 'Browser (chrome/edge/firefox)' @('chrome', 'edge', 'firefox')
            $id = Read-Host 'Extension ID'
            if ($c -eq '1') { Save-AndApply (Add-UcExtension $script:Config $b $id) } else { Save-AndApply (Remove-UcExtension $script:Config $b $id) }
        }
    }
}

function Show-UsersMenu {
    while ($true) {
        Write-Host "`n--- Restricted accounts: $(if ($script:Config.users) { $script:Config.users -join ', ' } else { '(none)' }) ---" -ForegroundColor Cyan
        $admins = @(Get-UcAdminMembers | ForEach-Object { $_.SID.Value })
        $candidates = @(Get-LocalUser | Where-Object { $_.Enabled -and $_.SID.Value -notin $admins -and $_.Name -notin $script:Config.users } | ForEach-Object Name)
        Write-Host "  Standard accounts on this PC: $(if ($candidates) { $candidates -join ', ' } else { '(none)' })" -ForegroundColor DarkGray
        Write-Host ' 1 Add an account'
        Write-Host ' 2 Remove an account (its restrictions are removed)'
        Write-Host ' 0 Back'
        $c = Read-Choice 'Choice' @('0', '1', '2')
        if ($c -eq '0') { return }
        Invoke-Safely {
            $script:User = Read-Host 'Account name'
            if ($c -eq '1') { Invoke-UcAction 'AddUser' } else { Invoke-UcAction 'RemoveUser' }
        }
    }
}

function Show-Menu {
    if ($PSScriptRoot -ne $script:UcInstallDir -and (Test-Path $script:InstalledScript)) {
        Write-Host "Note: an installed copy exists in $script:UcInstallDir; the games lock task uses that copy and its config." -ForegroundColor Yellow
    }
    while ($true) {
        Write-Host "`n=== UseControl ($(if ($script:Config.users) { $script:Config.users -join ', ' } else { 'no accounts yet' })) ===" -ForegroundColor Cyan
        Write-Host ' 1 Status'
        Write-Host ' 2 Apply restrictions'
        Write-Host ' 3 Remove all restrictions'
        Write-Host ' 4 Allow games for some hours'
        Write-Host ' 5 Block games again now'
        Write-Host ' 6 Programs (block, allow, detect games)'
        Write-Host ' 7 Browser extensions'
        Write-Host ' 8 Restricted accounts'
        Write-Host ' 9 Security check'
        Write-Host ' I Install / update in Program Files'
        Write-Host ' 0 Exit'
        $c = Read-Choice 'Choice' @('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'i', 'I')
        switch ($c) {
            '0' { return }
            '1' { Invoke-Safely { Invoke-UcAction 'Status' } }
            '2' { Invoke-Safely { Invoke-UcAction 'Apply' } }
            '3' { if ((Read-Host "Type YES to remove all restrictions") -ceq 'YES') { Invoke-Safely { Invoke-UcAction 'Undo' } } }
            '4' { Invoke-Safely { $h = Read-Host 'Hours (0.5 - 24)'; $script:Hours = [double]::Parse($h.Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture); Invoke-UcAction 'AllowGames' } }
            '5' { Invoke-Safely { Invoke-UcAction 'LockGames' } }
            '6' { Show-ProgramsMenu }
            '7' { Show-ExtensionMenu }
            '8' { Show-UsersMenu }
            '9' { Invoke-Safely { Invoke-UcAction 'Audit' } }
            { $_ -in 'i', 'I' } { Invoke-Safely { Invoke-UcAction 'Install' } }
        }
    }
}

try {
    if ($Action -eq 'Menu') { Show-Menu } else { Invoke-UcAction $Action }
} catch {
    Write-UcLog "ERROR in $Action : $($_.Exception.Message)"
    Write-Error $_.Exception.Message -ErrorAction Continue
    exit 1
}
