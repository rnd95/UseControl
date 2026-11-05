<#
.SYNOPSIS
  UseControl, window version (Windows Forms). Same functions as UseControl.ps1 (lib\*.ps1).
  Long actions run in the background so the window does not freeze.
#>
param([string]$ConfigPath)

$ErrorActionPreference = 'Stop'
$libDir = Join-Path $PSScriptRoot 'lib'
$libNames = 'Config', 'Scan', 'Hive', 'Browsers', 'Restrictions', 'Games', 'Audit', 'Status', 'Install'
foreach ($lib in $libNames) { . (Join-Path $libDir "$lib.ps1") }

Add-Type -AssemblyName System.Windows.Forms, System.Drawing, Microsoft.VisualBasic

if (-not (Test-UcIsAdmin)) {
    try {
        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "`"$PSCommandPath`"")
        if ($ConfigPath) { $argList += @('-ConfigPath', "`"$ConfigPath`"") }
        Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList $argList
    } catch {
        [Windows.Forms.MessageBox]::Show('Administrator rights are needed.', 'UseControl', 'OK', 'Warning') | Out-Null
    }
    return
}

try {
    if (-not $ConfigPath) { $ConfigPath = Resolve-UcConfigPath $PSScriptRoot }
    $script:Config = Get-UcConfig $ConfigPath
} catch {
    [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'UseControl', 'OK', 'Error') | Out-Null
    return
}
$script:InstalledScript = Join-Path $script:UcInstallDir 'UseControl.ps1'
$iconPath = Join-Path $PSScriptRoot 'uc.ico'

# --- background work ---------------------------------------------------------------------------------
# One runspace with the same functions loaded; one job at a time, polled by a timer on the UI thread.
$script:Runspace = [runspacefactory]::CreateRunspace()
$script:Runspace.Open()
$init = [powershell]::Create()
$init.Runspace = $script:Runspace
[void]$init.AddScript("`$ErrorActionPreference = 'Stop'; foreach (`$l in '$($libNames -join "','")') { . (Join-Path '$libDir' (`$l + '.ps1')) }")
[void]$init.Invoke()
$init.Dispose()

$script:Job = $null

function Write-Log([string]$Text, [string]$Color = 'Black') {
    $log.SelectionStart = $log.TextLength
    $log.SelectionColor = [Drawing.Color]::$Color
    $log.AppendText(("{0:HH:mm:ss}  {1}`r`n" -f (Get-Date), $Text))
    $log.ScrollToCaret()
}

function Start-Work([string]$Title, [string]$Script, [object[]]$Arguments, [scriptblock]$Done) {
    if ($script:Job) { Write-Log 'Wait: another action is still running.' 'DarkOrange'; return }
    $ps = [powershell]::Create()
    $ps.Runspace = $script:Runspace
    [void]$ps.AddScript($Script)
    foreach ($a in $Arguments) { [void]$ps.AddArgument($a) }
    $script:Job = @{ PS = $ps; Handle = $ps.BeginInvoke(); Done = $Done; Title = $Title }
    $tabs.Enabled = $false
    $form.Cursor = [Windows.Forms.Cursors]::WaitCursor
    Write-Log "$Title..." 'Gray'
    $timer.Start()
}

$timer = New-Object Windows.Forms.Timer
$timer.Interval = 200
$timer.Add_Tick({
    if (-not $script:Job -or -not $script:Job.Handle.IsCompleted) { return }
    $timer.Stop()
    $j = $script:Job
    $script:Job = $null
    $tabs.Enabled = $true
    $form.Cursor = [Windows.Forms.Cursors]::Default
    try {
        $result = $j.PS.EndInvoke($j.Handle)
        foreach ($w in $j.PS.Streams.Warning) { Write-Log "Warning: $w" 'DarkOrange' }
        foreach ($e in $j.PS.Streams.Error) { Write-Log "Error: $e" 'Red' }
        if ($j.Done) { & $j.Done $result }
    } catch {
        $msg = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
        Write-Log "$($j.Title) failed: $msg" 'Red'
    } finally { $j.PS.Dispose() }
})

# Scripts run in the background runspace (they receive the config path and other values as arguments).
$W = @{
    Status  = 'param($cp) Get-UcStatusText (Get-UcConfig $cp)'
    Apply   = 'param($cp) $c = Get-UcConfig $cp; if (-not $c.users) { return "Saved. No accounts listed yet, nothing applied." }
               $r = Invoke-UcApplyAll $c
               $m = ($r | ForEach-Object { "$($_.User): $($_.Blocked) blocked / $($_.Allowed) allowed folders, $($_.Names) names" }) -join "; "
               if (Test-UcGamesAllowedNow) { $m += " (games still allowed)" }; Write-UcLog "Applied: $m"; "Applied. $m"'
    Undo    = 'param($cp) $c = Get-UcConfig $cp; Set-UcGamesUntil $null; Unregister-ScheduledTask -TaskName $UcTaskName -Confirm:$false -ErrorAction SilentlyContinue
               Invoke-UcApplyAll $c -Undo | Out-Null; Write-UcLog "Restrictions removed"; "Restrictions removed for: $($c.users -join ", ")"'
    Allow   = 'param($cp, $h, $inst) $u = Grant-UcGames (Get-UcConfig $cp) $h $inst; "Games allowed until {0:yyyy-MM-dd HH:mm}" -f $u'
    Lock    = 'param($cp) Revoke-UcGames (Get-UcConfig $cp); "Games blocked again"'
    Audit   = 'param($cp) Invoke-UcAudit (Get-UcConfig $cp)'
    Detect  = 'param($cp) Find-UcGameFolders (Get-UcConfig $cp)'
    UndoOne = 'param($cp, $u) Set-UcAccountRestrictions (Get-UcConfig $cp) (Get-UcAccount $u) $null @() -Undo; "Restrictions removed for $u"'
    Install = 'param($src) Install-Uc $src | Format-List | Out-String'
}

function Invoke-Apply { Start-Work 'Applying' $W.Apply @($ConfigPath) { param($r) Write-Log "$r" 'DarkGreen'; Update-Status } }

# Save the config, then apply it in the background.
function Save-AndApply($cfg) {
    Save-UcConfig $cfg $ConfigPath
    $script:Config = Get-UcConfig $ConfigPath
    Update-Lists
    Invoke-Apply
}

function Invoke-Edit([scriptblock]$Edit) {
    try { & $Edit } catch { Write-Log $_.Exception.Message 'Red'; [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'UseControl', 'OK', 'Warning') | Out-Null }
}

# --- layout helpers ------------------------------------------------------------------------------------
function New-Button([string]$Text, [scriptblock]$OnClick, [int]$Width = 170) {
    $b = New-Object Windows.Forms.Button
    $b.Text = $Text; $b.Width = $Width; $b.Height = 30; $b.Margin = '4,4,4,4'
    $b.Add_Click($OnClick)
    $b
}
function New-Flow { $f = New-Object Windows.Forms.FlowLayoutPanel; $f.Dock = 'Top'; $f.AutoSize = $true; $f.WrapContents = $true; $f.Padding = '4,4,4,4'; $f }
function New-Label([string]$Text) { $l = New-Object Windows.Forms.Label; $l.Text = $Text; $l.AutoSize = $true; $l.Margin = '6,10,6,4'; $l }
function New-Tab([string]$Text) { $t = New-Object Windows.Forms.TabPage; $t.Text = $Text; $t.Padding = '6,6,6,6'; $t }
function New-Help([string]$Text, [int]$Height = 60) {
    $l = New-Object Windows.Forms.Label
    $l.Dock = 'Bottom'; $l.Height = $Height; $l.Padding = '8,6,8,6'; $l.ForeColor = [Drawing.Color]::DimGray; $l.Text = $Text
    $l
}

# --- window ----------------------------------------------------------------------------------------------
[Windows.Forms.Application]::EnableVisualStyles()
$form = New-Object Windows.Forms.Form
$form.Text = 'UseControl'
$form.Size = New-Object Drawing.Size(1000, 720)
$form.MinimumSize = New-Object Drawing.Size(780, 540)
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object Drawing.Font('Segoe UI', 9.5)
$form.AutoScaleMode = 'Dpi'
if (Test-Path $iconPath) { $form.Icon = New-Object Drawing.Icon($iconPath) }

$log = New-Object Windows.Forms.RichTextBox
$log.Dock = 'Bottom'; $log.Height = 140; $log.ReadOnly = $true; $log.BackColor = [Drawing.Color]::White
$log.Font = New-Object Drawing.Font('Consolas', 9)

$tabs = New-Object Windows.Forms.TabControl
$tabs.Dock = 'Fill'

# Status ---------------------------------------------------------------------------------------------------
$tStatus = New-Tab 'Status'
$statusBox = New-Object Windows.Forms.TextBox
$statusBox.Multiline = $true; $statusBox.ReadOnly = $true; $statusBox.Dock = 'Fill'; $statusBox.ScrollBars = 'Vertical'
$statusBox.Font = New-Object Drawing.Font('Consolas', 10); $statusBox.BackColor = [Drawing.Color]::White
function Update-Status { Start-Work 'Reading status' $W.Status @($ConfigPath) { param($r) $statusBox.Text = ("$r").Trim() } }
$fs = New-Flow
$fs.Controls.Add((New-Button 'Refresh' { Update-Status } 110))
$fs.Controls.Add((New-Button 'Apply restrictions' { Invoke-Apply }))
$fs.Controls.Add((New-Button 'Remove all restrictions' {
    $q = [Windows.Forms.MessageBox]::Show("Remove ALL restrictions for $($script:Config.users -join ', ')?", 'UseControl', 'YesNo', 'Warning')
    if ($q -eq 'Yes') { Start-Work 'Removing restrictions' $W.Undo @($ConfigPath) { param($r) Write-Log "$r" 'DarkOrange'; Update-Status } }
}))
$fs.Controls.Add((New-Button 'Install / update copy' {
    if ($PSScriptRoot -eq $script:UcInstallDir) { Write-Log "Already running the installed copy ($script:UcInstallDir)." 'Gray'; return }
    Start-Work 'Installing' $W.Install @($PSScriptRoot) { param($r) Write-Log ("Installed.`r`n" + "$r".Trim()) 'DarkGreen'; Update-Status }
}))
$tStatus.Controls.Add($statusBox)
$tStatus.Controls.Add($fs)

# Accounts -------------------------------------------------------------------------------------------------
$tUsers = New-Tab 'Accounts'
$userList = New-Object Windows.Forms.ListBox
$userList.Dock = 'Fill'; $userList.Font = New-Object Drawing.Font('Consolas', 11)
$fu = New-Flow
$fu.Controls.Add((New-Button 'Add account...' {
    $admins = @(Get-UcAdminMembers | ForEach-Object { $_.SID.Value })
    $candidates = @(Get-LocalUser | Where-Object { $_.Enabled -and $_.SID.Value -notin $admins -and $_.Name -notin $script:Config.users } | ForEach-Object Name)
    $n = [Microsoft.VisualBasic.Interaction]::InputBox("Standard account to restrict.`r`n`r`nOn this PC: $(if ($candidates) { $candidates -join ', ' } else { '(none)' })", 'Add account', $(if ($candidates) { $candidates[0] } else { '' }))
    if ($n) { Invoke-Edit { Get-UcAccount $n | Out-Null; Save-AndApply (Add-UcUser $script:Config $n) } }
} 150))
$fu.Controls.Add((New-Button 'Remove selected' {
    $u = $userList.SelectedItem
    if (-not $u) { return }
    if ([Windows.Forms.MessageBox]::Show("Stop restricting $u and remove its restrictions?", 'UseControl', 'YesNo', 'Question') -ne 'Yes') { return }
    Invoke-Edit {
        $c = Remove-UcUser $script:Config "$u"
        Save-UcConfig $c $ConfigPath; $script:Config = Get-UcConfig $ConfigPath; Update-Lists
        Start-Work "Removing restrictions of $u" $W.UndoOne @($ConfigPath, "$u") { param($r) Write-Log "$r" 'DarkOrange'; Update-Status }
    }
} 150))
$tUsers.Controls.Add($userList)
$tUsers.Controls.Add((New-Help "Restrictions apply only to the accounts listed here; administrators are never restricted.`r`nAn account must have signed in once (so that its profile exists) before it can be added."))
$tUsers.Controls.Add($fu)

# Games ----------------------------------------------------------------------------------------------------
$tGames = New-Tab 'Games'
$fg = New-Flow
$fg.Controls.Add((New-Label 'Allow games for'))
$hours = New-Object Windows.Forms.NumericUpDown
$hours.Minimum = 0.5; $hours.Maximum = 24; $hours.DecimalPlaces = 1; $hours.Increment = 0.5; $hours.Value = 1; $hours.Width = 70; $hours.Margin = '4,8,4,4'
$fg.Controls.Add($hours)
$fg.Controls.Add((New-Label 'hours'))
$fg.Controls.Add((New-Button 'Allow games' {
    Start-Work 'Allowing games' $W.Allow @($ConfigPath, [double]$hours.Value, $script:InstalledScript) { param($r) Write-Log "$r" 'DarkGreen'; Update-Status }
} 140))
$fg.Controls.Add((New-Button 'Block games now' { Start-Work 'Blocking games' $W.Lock @($ConfigPath) { param($r) Write-Log "$r" 'DarkGreen'; Update-Status } } 140))
$tGames.Controls.Add((New-Help ("Game folders (category 'games') become allowed and game program names are no longer blocked.`r`n" +
    "A scheduled task (SYSTEM) blocks them again at the end time and at every start of the PC.`r`n" +
    "A signed-in account may need to sign out and back in to notice the change.`r`n" +
    "Online games blocked by a DNS filter (AdGuard, Pi-hole, router) are not affected: use the filter's own schedule.`r`n" +
    "Needs the installed copy (Status > Install / update copy).") 120))
$tGames.Controls.Add($fg)

# Programs ---------------------------------------------------------------------------------------------------
$tProg = New-Tab 'Programs'
$list = New-Object Windows.Forms.ListView
$list.View = 'Details'; $list.FullRowSelect = $true; $list.Dock = 'Fill'; $list.HideSelection = $false
[void]$list.Columns.Add('Rule', 110); [void]$list.Columns.Add('Folder or program', 480); [void]$list.Columns.Add('Category', 90); [void]$list.Columns.Add('Note', 220)
$fp = New-Flow
$fp.Controls.Add((New-Label 'Category:'))
$cat = New-Object Windows.Forms.ComboBox
$cat.DropDownStyle = 'DropDownList'; $cat.Width = 90; $cat.Margin = '4,8,4,4'
[void]$cat.Items.AddRange(@('games', 'vpn', 'other', 'tools')); $cat.SelectedIndex = 0
$fp.Controls.Add($cat)
$fp.Controls.Add((New-Button 'Block folder...' {
    $d = New-Object Windows.Forms.FolderBrowserDialog
    $d.Description = 'Programs in this folder (and below) will not start for restricted accounts.'
    if ($d.ShowDialog($form) -eq 'OK') { Invoke-Edit { Save-AndApply (Add-UcBlockedPath $script:Config $d.SelectedPath $cat.SelectedItem) } }
} 125))
$fp.Controls.Add((New-Button 'Block program...' {
    $d = New-Object Windows.Forms.OpenFileDialog
    $d.Filter = 'Programs (*.exe)|*.exe|All files (*.*)|*.*'
    $d.Title = 'This program name will be blocked wherever it is'
    if ($d.ShowDialog($form) -eq 'OK') { Invoke-Edit { Save-AndApply (Add-UcBlockedName $script:Config ([IO.Path]::GetFileName($d.FileName)) $cat.SelectedItem) } }
} 125))
$fp.Controls.Add((New-Button 'Block by name...' {
    $n = [Microsoft.VisualBasic.Interaction]::InputBox('Program file name, for example game.exe', 'Block by name', '')
    if ($n) { Invoke-Edit { Save-AndApply (Add-UcBlockedName $script:Config $n $cat.SelectedItem) } }
} 125))
$fp.Controls.Add((New-Button 'Allow folder...' {
    $d = New-Object Windows.Forms.FolderBrowserDialog
    $d.Description = 'A program folder that should run although it is in a blocked place (for example C:\Tools).'
    if ($d.ShowDialog($form) -eq 'OK') { Invoke-Edit { Save-AndApply (Add-UcAllowedPath $script:Config $d.SelectedPath) } }
} 125))
$fp.Controls.Add((New-Button 'Detect games' {
    Start-Work 'Looking for game folders' $W.Detect @($ConfigPath) {
        param($found)
        $found = @($found | ForEach-Object { "$_" })
        if (-not $found) { Write-Log 'No new game folders found.' 'DarkGreen'; return }
        $q = [Windows.Forms.MessageBox]::Show("Block these as games?`r`n`r`n" + ($found -join "`r`n"), 'UseControl', 'YesNo', 'Question')
        if ($q -eq 'Yes') { Invoke-Edit { $c = $script:Config; foreach ($p in $found) { $c = Add-UcBlockedPath $c $p 'games' }; Save-AndApply $c } }
    }
} 125))
$fp.Controls.Add((New-Button 'Remove selected' {
    $sel = @($list.SelectedItems)
    if (-not $sel) { return }
    Invoke-Edit {
        $c = $script:Config
        foreach ($i in $sel) {
            $v = $i.SubItems[1].Text
            switch ($i.Text) {
                'Blocked folder' { $c = Remove-UcBlockedPath $c $v }
                'Allowed folder' { $c = Remove-UcAllowedPath $c $v }
                'Blocked name'   { $c = Remove-UcBlockedName $c $v }
            }
        }
        Save-AndApply $c
    }
} 125))
$tProg.Controls.Add($list)
$tProg.Controls.Add((New-Help ("Also blocked automatically (see the console: UseControl.ps1 -Action Preview): the account's profile and Downloads, the system drive root, " +
    "drives and program folders that ordinary users can write to, USB and network drive letters, Recycle Bins. Windows and Program Files stay allowed.")))
$tProg.Controls.Add($fp)

# Extensions -------------------------------------------------------------------------------------------------
$tExt = New-Tab 'Browser extensions'
$fe = New-Flow
$fe.Controls.Add((New-Label 'Browser:'))
$browser = New-Object Windows.Forms.ComboBox
$browser.DropDownStyle = 'DropDownList'; $browser.Width = 100; $browser.Margin = '4,8,4,4'
[void]$browser.Items.AddRange(@('chrome', 'edge', 'firefox')); $browser.SelectedIndex = 0
$fe.Controls.Add($browser)
$fe.Controls.Add((New-Label 'Extension ID:'))
$extId = New-Object Windows.Forms.TextBox
$extId.Width = 300; $extId.Margin = '4,8,4,4'
$fe.Controls.Add($extId)
$fe.Controls.Add((New-Button 'Allow' { Invoke-Edit { Save-AndApply (Add-UcExtension $script:Config $browser.SelectedItem $extId.Text); $extId.Text = '' } } 90))
$fe.Controls.Add((New-Button 'Remove selected' {
    if ($extList.SelectedItem) { $p = "$($extList.SelectedItem)" -split ':\s+', 2; Invoke-Edit { Save-AndApply (Remove-UcExtension $script:Config $p[0] $p[1]) } }
} 140))
$extList = New-Object Windows.Forms.ListBox
$extList.Dock = 'Fill'; $extList.Font = New-Object Drawing.Font('Consolas', 10)
$tExt.Controls.Add($extList)
$tExt.Controls.Add((New-Help ("Chrome/Edge: the ID is the 32 letters at the end of the extension's store address (chromewebstore.google.com/detail/name/<ID>).`r`n" +
    "Firefox: the add-on ID from about:support > Add-ons (for example uBlock0@raymondhill.net). All other extensions stay blocked.")))
$tExt.Controls.Add($fe)

# Security check ---------------------------------------------------------------------------------------------
$tAudit = New-Tab 'Security check'
$audit = New-Object Windows.Forms.ListView
$audit.View = 'Details'; $audit.FullRowSelect = $true; $audit.Dock = 'Fill'
[void]$audit.Columns.Add('Result', 70); [void]$audit.Columns.Add('Check', 220); [void]$audit.Columns.Add('Detail', 660)
$audit.Add_DoubleClick({ if ($audit.SelectedItems.Count) { $i = $audit.SelectedItems[0]; [Windows.Forms.MessageBox]::Show($i.SubItems[2].Text, $i.SubItems[1].Text) | Out-Null } })
$fa = New-Flow
$fa.Controls.Add((New-Button 'Run the check' {
    Start-Work 'Security check (up to a minute)' $W.Audit @($ConfigPath) {
        param($rows)
        $audit.Items.Clear()
        foreach ($r in $rows) {
            $it = New-Object Windows.Forms.ListViewItem($r.Result)
            [void]$it.SubItems.Add($r.Check); [void]$it.SubItems.Add($r.Detail)
            $it.ForeColor = switch ($r.Result) { 'OK' { [Drawing.Color]::DarkGreen } 'WARN' { [Drawing.Color]::DarkOrange } default { [Drawing.Color]::Red } }
            [void]$audit.Items.Add($it)
        }
        $f = @($rows | Where-Object Result -eq 'FAIL').Count; $w = @($rows | Where-Object Result -eq 'WARN').Count
        Write-Log ("Security check: {0} checks, {1} FAIL, {2} WARN (double-click a row for details)" -f @($rows).Count, $f, $w) $(if ($f) { 'Red' } elseif ($w) { 'DarkOrange' } else { 'DarkGreen' })
    }
} 150))
$tAudit.Controls.Add($audit)
$tAudit.Controls.Add($fa)

function Add-Row([string]$Rule, [string]$Value, [string]$Category, [string]$Note) {
    $it = New-Object Windows.Forms.ListViewItem($Rule)
    [void]$it.SubItems.Add($Value); [void]$it.SubItems.Add($Category); [void]$it.SubItems.Add($Note)
    if ($Rule -eq 'Allowed folder') { $it.ForeColor = [Drawing.Color]::DarkGreen }
    [void]$list.Items.Add($it)
}

function Update-Lists {
    $list.BeginUpdate(); $list.Items.Clear()
    foreach ($e in $script:Config.blockedPaths) { Add-Row 'Blocked folder' $e.path $e.category "$($e.note)" }
    foreach ($e in $script:Config.allowedPaths) { Add-Row 'Allowed folder' $e.path '' "$($e.note)" }
    foreach ($e in $script:Config.blockedNames) { Add-Row 'Blocked name' $e.name $e.category "$($e.note)" }
    $list.EndUpdate()
    $extList.Items.Clear()
    foreach ($b in 'chrome', 'edge', 'firefox') { foreach ($id in $script:Config.browsers.$b.extensionsAllowed) { [void]$extList.Items.Add("${b}: $id") } }
    $userList.Items.Clear()
    foreach ($u in $script:Config.users) { [void]$userList.Items.Add($u) }
    $form.Text = "UseControl - " + $(if ($script:Config.users) { $script:Config.users -join ', ' } else { 'no accounts yet' })
}

$tabs.TabPages.AddRange(@($tStatus, $tUsers, $tGames, $tProg, $tExt, $tAudit))
$form.Controls.Add($tabs)
$form.Controls.Add($log)

$form.Add_Shown({
    Update-Lists
    Write-Log "Config: $ConfigPath"
    if ($PSScriptRoot -ne $script:UcInstallDir) {
        Write-Log "Running from $PSScriptRoot. 'Install / update copy' puts the tool in $script:UcInstallDir (needed for games time)." 'DarkOrange'
    }
    if (-not $script:Config.users) { Write-Log 'No accounts to restrict yet: Accounts > Add account.' 'DarkOrange'; $tabs.SelectedTab = $tUsers }
    Update-Status
})
$form.Add_FormClosing({ param($s, $e) if ($script:Job) { $e.Cancel = $true; Write-Log 'Wait until the current action finishes.' 'DarkOrange' } })

[void]$form.ShowDialog()
$script:Runspace.Close()
