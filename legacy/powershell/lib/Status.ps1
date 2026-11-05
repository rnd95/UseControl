# Short status for the menu and the window.

function Get-UcStatusText($Config) {
    $lines = @()
    $until = Get-UcGamesUntil
    $task = Get-ScheduledTask -TaskName $script:UcTaskName -ErrorAction SilentlyContinue
    $store = (Get-ItemProperty $script:UcAppxKey -ErrorAction SilentlyContinue).BlockNonAdminUserInstall -eq 1
    $lines += "Games:          " + $(if ($until -and $until -gt (Get-Date)) { "ALLOWED until {0:yyyy-MM-dd HH:mm}" -f $until } else { 'blocked (if any are listed)' })
    $lines += "Lock task:      " + $(if ($task) { "$($task.State)" } else { 'none' })
    $lines += "Store installs: " + $(if ($store) { 'administrators only' } else { 'allowed for everyone' })
    $lines += "Installed:      " + $(if (Test-Path (Join-Path $script:UcInstallDir 'UseControl.ps1')) { $script:UcInstallDir } else { 'no' })
    $lines += ''
    if (-not $Config.users) { $lines += 'No restricted accounts yet: add one.' }
    foreach ($u in $Config.users) {
        try {
            $st = Get-UcRestrictionState (Get-UcAccount $u)
            $cats = ($st.RulesByCategory.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ', '
            $lines += "[$u] " + $(if ($st.SignedIn) { 'signed in' } else { 'signed out' })
            $lines += "    restrictions:   " + $(if ($st.Applied) { 'applied' } else { 'NOT applied' })
            $lines += "    blocked folders: $($st.BlockedRules) ($cats)"
            $lines += "    allowed folders: $($st.AllowedRules)"
            $lines += "    blocked names:   $($st.NameRules)"
            $lines += "    policy keys read-only for the user: $($st.Locked)"
        } catch { $lines += "[$u] $($_.Exception.Message)" }
    }
    $lines -join "`r`n"
}
