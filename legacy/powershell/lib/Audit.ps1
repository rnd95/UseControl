# Security check of the PC. Read-only: it reports, it never changes anything.

function New-UcCheck([string]$Check, [string]$Result, [string]$Detail) {
    [pscustomobject]@{ Result = $Result; Check = $Check; Detail = $Detail }
}

function Invoke-UcAudit($Config) {
    $out = New-Object System.Collections.Generic.List[object]
    $accounts = @()
    foreach ($u in $Config.users) {
        try { $accounts += Get-UcAccount $u } catch { $out.Add((New-UcCheck "Account $u" 'FAIL' $_.Exception.Message)) }
    }
    $weak = Get-UcWeakSids @($accounts | ForEach-Object Sid)

    # 1. Services whose program (or its folder) ordinary users can change = a way to become SYSTEM.
    $bad = @(); $seen = @{}
    foreach ($s in Get-CimInstance Win32_Service) {
        $p = $s.PathName
        if (-not $p) { continue }
        $exe = if ($p.StartsWith('"')) { $p.Split('"')[1] } else { ($p -split '\s+(?=[-/])')[0] }
        $exe = [Environment]::ExpandEnvironmentVariables($exe)
        if ($exe -match '^\\\?\?\\') { $exe = $exe.Substring(4) }
        if ($exe -match '^\\SystemRoot\\') { $exe = $exe -replace '^\\SystemRoot', $env:SystemRoot }
        if ($exe -match '^system32\\') { $exe = Join-Path $env:SystemRoot $exe }
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { continue }
        foreach ($t in @($exe, (Split-Path $exe -Parent))) {
            $key = $t.ToLower()
            if (-not $seen.ContainsKey($key)) { $seen[$key] = Get-UcWritableBy $t $weak }
            if ($seen[$key]) { $bad += "$($s.Name): $t ($($seen[$key] -join ', '))" }
        }
    }
    if ($bad) { $out.Add((New-UcCheck 'Services' 'FAIL' ("Writable by ordinary users: " + (($bad | Select-Object -Unique) -join '; ')))) }
    else { $out.Add((New-UcCheck 'Services' 'OK' "No service program or folder is writable by ordinary users ($($seen.Count) checked).")) }

    # 2. Writable places: blocked automatically, or open if the "auto" switches are off.
    $auto = Get-UcAutoPaths $Config
    $autoOn = $Config.auto.writableProgramFolders -and $Config.auto.writableDriveRoots -and $Config.auto.systemDriveRoot
    $writableFolders = @($auto | Where-Object { $_.Why -like 'program folder*' -or $_.Why -like 'drive writable*' } | ForEach-Object Path)
    $out.Add((New-UcCheck 'Writable program folders' $(if ($autoOn) { 'OK' } else { 'WARN' }) $(
        if ($writableFolders) { "Programs cannot start from these (blocked automatically): $($writableFolders -join '; ')" }
        elseif ($autoOn) { 'None found' } else { 'Automatic blocking is switched off in config.json (auto)' })))

    $openAllowed = @($Config.allowedPaths | ForEach-Object { ConvertTo-UcPath $_.path } | Where-Object { (Test-Path -LiteralPath $_) -and (Get-UcWritableBy $_ $weak) })
    $out.Add((New-UcCheck 'Allowed folders' $(if ($openAllowed) { 'WARN' } else { 'OK' }) $(
        if ($openAllowed) { "Allowed but writable by ordinary users (a copied program would run): $($openAllowed -join '; ')" }
        else { "$(@($Config.allowedPaths).Count) allowed folder(s), none writable by ordinary users" })))

    # 3. Private folders: nobody but administrators and SYSTEM (and whoever owns them).
    foreach ($f in $Config.audit.privateFolders) {
        if (-not (Test-Path -LiteralPath $f)) { $out.Add((New-UcCheck "Private: $f" 'WARN' 'Folder not found.')); continue }
        $acl = Get-Acl -LiteralPath $f
        $others = @($acl.Access | Where-Object { $_.AccessControlType -eq 'Allow' -and ((ConvertTo-UcSid $_.IdentityReference) -in $weak) } |
                    ForEach-Object { "$($_.IdentityReference.Value) $($_.FileSystemRights)" })
        if ($others) { $out.Add((New-UcCheck "Private: $f" 'FAIL' ("Others have access: " + ($others -join '; ')))) }
        else { $out.Add((New-UcCheck "Private: $f" 'OK' (($acl.Access | ForEach-Object { $_.IdentityReference.Value } | Select-Object -Unique) -join ', '))) }
    }

    # 4. Accounts
    $admins = @(Get-UcAdminMembers | ForEach-Object { ($_.Name -split '\\')[-1] })
    $builtin = Get-LocalUser | Where-Object { $_.SID.Value -match '-500$' }
    if ($Config.audit.admins) {
        $extra = @($admins | Where-Object { $_ -notin @($Config.audit.admins) -and $_ -ne $builtin.Name })
        $out.Add((New-UcCheck 'Administrators group' $(if ($extra) { 'FAIL' } else { 'OK' }) $(if ($extra) { "Unexpected members: $($extra -join ', ')" } else { $admins -join ', ' })))
    } else {
        $out.Add((New-UcCheck 'Administrators group' 'OK' "$($admins -join ', ') (list the expected ones in audit.admins to be warned about others)"))
    }
    $restrictedAdmins = @($Config.users | Where-Object { $_ -in $admins })
    if ($restrictedAdmins) { $out.Add((New-UcCheck 'Restricted accounts' 'FAIL' "Administrators cannot be restricted: $($restrictedAdmins -join ', ')")) }

    $out.Add((New-UcCheck 'Built-in Administrator' $(if ($builtin.Enabled) { 'WARN' } else { 'OK' }) $(if ($builtin.Enabled) { 'Enabled' } else { 'Disabled' })))

    $noPwd = @(Get-LocalUser | Where-Object { $_.Enabled -and -not $_.PasswordRequired } | ForEach-Object Name)
    $out.Add((New-UcCheck 'Empty passwords allowed' $(if ($noPwd) { 'WARN' } else { 'OK' }) $(if ($noPwd) { "net user <name> /passwordreq:yes for: $($noPwd -join ', ')" } else { 'No enabled account may have an empty password' })))

    if ($Config.audit.hiddenAccounts) {
        $hiddenList = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList' -ErrorAction SilentlyContinue
        $visible = @($Config.audit.hiddenAccounts | Where-Object { -not $hiddenList -or $hiddenList.$_ -ne 0 })
        $out.Add((New-UcCheck 'Hidden accounts' $(if ($visible) { 'WARN' } else { 'OK' }) $(if ($visible) { "Shown on the sign-in screen: $($visible -join ', ')" } else { ($Config.audit.hiddenAccounts -join ', ') + ' hidden' })))
    }

    # 5. UAC
    $sys = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    $uacOk = $sys.EnableLUA -eq 1 -and $sys.ConsentPromptBehaviorAdmin -in 1, 2, 5 -and $sys.PromptOnSecureDesktop -eq 1 -and $sys.ConsentPromptBehaviorUser -in 1, 3
    $out.Add((New-UcCheck 'UAC' $(if ($uacOk) { 'OK' } else { 'WARN' }) ("EnableLUA={0} ConsentPromptBehaviorAdmin={1} PromptOnSecureDesktop={2} ConsentPromptBehaviorUser={3} (want 1, 5, 1, 3)" -f $sys.EnableLUA, $sys.ConsentPromptBehaviorAdmin, $sys.PromptOnSecureDesktop, $sys.ConsentPromptBehaviorUser)))

    $aie = (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Installer' -ErrorAction SilentlyContinue).AlwaysInstallElevated
    $out.Add((New-UcCheck 'AlwaysInstallElevated' $(if ($aie -eq 1) { 'FAIL' } else { 'OK' }) $(if ($aie -eq 1) { 'Any .msi installs as SYSTEM' } else { 'Off' })))

    # 6. Remote desktop
    $ts = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
    if ($ts.fDenyTSConnections -eq 1) { $out.Add((New-UcCheck 'RDP' 'OK' 'Off')) }
    else {
        $nla = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp').UserAuthentication
        $rules = @(Get-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' })
        $any = @($rules | Where-Object { ($_ | Get-NetFirewallAddressFilter).RemoteAddress -contains 'Any' })
        $scopes = ($rules | ForEach-Object { ($_ | Get-NetFirewallAddressFilter).RemoteAddress } | Select-Object -Unique) -join ', '
        $res = if ($nla -ne 1 -or $any -or -not $rules) { 'WARN' } else { 'OK' }
        $out.Add((New-UcCheck 'RDP' $res ("On, NLA={0}, allowed from: {1}" -f $nla, $(if ($scopes) { $scopes } else { 'no enabled Remote Desktop firewall rule found' }))))
    }

    # 7. Remote access tools and protection
    $remote = @(Get-Service -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match 'ScreenConnect|AnyDesk|TeamViewer|RustDesk|Mesh Agent|Radmin|rserver|Splashtop|vncserver|tvnserver|uvnc|LogMeIn|Ammyy|Atera|Kaseya'
    } | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Text = "$($_.Name) ($($_.Status))" } })
    $unknown = @($remote | Where-Object { $_.Name -notin @($Config.audit.knownRemoteServices) } | ForEach-Object Text)
    $known = @($remote | Where-Object { $_.Name -in @($Config.audit.knownRemoteServices) } | ForEach-Object Text)
    $out.Add((New-UcCheck 'Remote access services' $(if ($unknown) { 'WARN' } else { 'OK' }) $(
        if ($unknown) { "Not in audit.knownRemoteServices, make sure you know them: $($unknown -join ', ')" + $(if ($known) { "; known: $($known -join ', ')" } else { '' }) }
        elseif ($known) { "Known: $($known -join ', ')" } else { 'None found' })))

    try {
        $mp = Get-MpComputerStatus -ErrorAction Stop
        $out.Add((New-UcCheck 'Microsoft Defender' $(if ($mp.RealTimeProtectionEnabled) { 'OK' } else { 'WARN' }) ("Real-time protection: {0}, signatures {1:yyyy-MM-dd}" -f $mp.RealTimeProtectionEnabled, $mp.AntivirusSignatureLastUpdated)))
    } catch { $out.Add((New-UcCheck 'Microsoft Defender' 'WARN' 'Status unavailable (another antivirus?)')) }

    $fwOff = @(Get-NetFirewallProfile | Where-Object { -not $_.Enabled } | ForEach-Object Name)
    $out.Add((New-UcCheck 'Firewall' $(if ($fwOff) { 'WARN' } else { 'OK' }) $(if ($fwOff) { "Off for: $($fwOff -join ', ')" } else { 'On for all profiles' })))

    # 8. The restricted accounts
    foreach ($acc in $accounts) {
        try {
            $st = Get-UcRestrictionState $acc
            $res = if (-not $st.Applied) { 'FAIL' } elseif (-not $st.Locked) { 'WARN' } else { 'OK' }
            $out.Add((New-UcCheck "Restrictions: $($acc.User)" $res ("{0} blocked and {1} allowed folders, {2} names, policy keys locked={3}" -f $st.BlockedRules, $st.AllowedRules, $st.NameRules, $st.Locked)))
        } catch { $out.Add((New-UcCheck "Restrictions: $($acc.User)" 'WARN' $_.Exception.Message)) }
    }
    if (-not $Config.users) { $out.Add((New-UcCheck 'Restricted accounts' 'WARN' 'No accounts listed in config.json (users)')) }

    $out.ToArray()
}
