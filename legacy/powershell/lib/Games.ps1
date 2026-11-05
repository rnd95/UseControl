# Games for a limited time: until a moment stored in state.json, game folders are allowed and game
# names are not blocked. A scheduled task (SYSTEM) blocks them again at that moment and at every start.

$script:UcTaskName = 'UseControl games lock'

function Get-UcStatePath { Join-Path $script:UcDataDir 'state.json' }

# The data folder is readable and writable only by Administrators and SYSTEM.
function Initialize-UcDataDir {
    if (-not (Test-Path $script:UcDataDir)) { New-Item -ItemType Directory -Path $script:UcDataDir | Out-Null }
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inh = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    foreach ($s in 'S-1-5-32-544', 'S-1-5-18') {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            (New-Object Security.Principal.SecurityIdentifier($s)), 'FullControl', $inh, 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $script:UcDataDir -AclObject $acl
}

function Get-UcGamesUntil {
    $f = Get-UcStatePath
    if (-not (Test-Path $f)) { return $null }
    $s = [IO.File]::ReadAllText($f) | ConvertFrom-Json
    if ($s.gamesAllowedUntil) { [DateTime]::Parse($s.gamesAllowedUntil, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind) } else { $null }
}

function Set-UcGamesUntil($Until) {
    Initialize-UcDataDir
    $f = Get-UcStatePath
    if ($null -eq $Until) { Remove-Item $f -ErrorAction SilentlyContinue; return }
    $json = @{ gamesAllowedUntil = $Until.ToString('o', [Globalization.CultureInfo]::InvariantCulture) } | ConvertTo-Json
    [IO.File]::WriteAllText($f, $json)
}

function Test-UcGamesAllowedNow {
    $until = Get-UcGamesUntil
    [bool]($until -and $until -gt (Get-Date))
}

function Write-UcLog([string]$Message) {
    try {
        Initialize-UcDataDir
        Add-Content -Path (Join-Path $script:UcDataDir 'log.txt') -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $Message)
    } catch { }
}

function Grant-UcGames($Config, [double]$Hours, [string]$InstalledScript) {
    if ($Hours -le 0 -or $Hours -gt 24) { throw 'Hours must be between 0 and 24.' }
    if (-not (Test-Path -LiteralPath $InstalledScript)) {
        throw "Install the tool first (Install): the lock task may only run the copy in $script:UcInstallDir."
    }
    $until = (Get-Date).AddHours($Hours)
    Set-UcGamesUntil $until
    Invoke-UcApplyAll $Config | Out-Null

    $action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$InstalledScript`" -Action GamesCheck"
    $triggers = @((New-ScheduledTaskTrigger -Once -At $until.AddSeconds(5)), (New-ScheduledTaskTrigger -AtStartup))
    $principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 10)
    Register-ScheduledTask -TaskName $script:UcTaskName -Action $action -Trigger $triggers -Principal $principal -Settings $settings -Force | Out-Null

    Write-UcLog "Games allowed until $until"
    $until
}

function Revoke-UcGames($Config) {
    Set-UcGamesUntil $null
    Invoke-UcApplyAll $Config | Out-Null
    Unregister-ScheduledTask -TaskName $script:UcTaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-UcLog 'Games blocked again'
}

# Run by the scheduled task: blocks games again once the time is over, otherwise does nothing.
function Invoke-UcGamesCheck($Config) {
    if (Test-UcGamesAllowedNow) { return 'still allowed' }
    Revoke-UcGames $Config
    'locked'
}
