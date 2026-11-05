# Copies the tool to Program Files\UseControl (only administrators can change it there), creates the
# data folder and a desktop shortcut to the window for the administrator who runs it.

$script:UcFiles = @('UseControl.ps1', 'UseControl-GUI.ps1', 'README.md', 'uc.ico', 'config.sample.json')

# config.json next to the scripts; created from config.sample.json on first use.
function Resolve-UcConfigPath([string]$Dir) {
    $cfg = Join-Path $Dir 'config.json'
    if (-not (Test-Path $cfg)) {
        $sample = Join-Path $Dir 'config.sample.json'
        if (-not (Test-Path $sample)) { throw "Neither config.json nor config.sample.json found in $Dir." }
        Copy-Item $sample $cfg
        Write-Warning "Created $cfg from the sample. Add the accounts to restrict (users)."
    }
    $cfg
}

# Removes an install of the earlier version (Family-PC).
function Remove-UcLegacyInstall {
    $removed = @()
    if (Get-ScheduledTask -TaskName 'Family-PC games lock' -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName 'Family-PC games lock' -Confirm:$false; $removed += 'task Family-PC games lock'
    }
    foreach ($p in (Join-Path $env:ProgramFiles 'Family-PC'), (Join-Path $env:ProgramData 'Family-PC'), (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Family-PC.lnk')) {
        if (Test-Path $p) { Remove-Item $p -Recurse -Force; $removed += $p }
    }
    $removed
}

function Install-Uc([string]$SourceDir) {
    $dest = $script:UcInstallDir
    $src = (Resolve-Path $SourceDir).Path.TrimEnd('\')
    if ($src -eq $dest.TrimEnd('\')) { throw "Already running from $dest. Run Install from the project folder." }

    New-Item -ItemType Directory -Path $dest, "$dest\lib" -Force | Out-Null
    foreach ($f in $script:UcFiles) { if (Test-Path "$src\$f") { Copy-Item "$src\$f" "$dest\$f" -Force } }
    Copy-Item "$src\lib\*.ps1" "$dest\lib\" -Force

    # The config: the project's config.json (or the sample); keep a backup of an installed one that differs.
    $cfgNew = Resolve-UcConfigPath $src
    $cfgOld = "$dest\config.json"
    $backup = $null
    if ((Test-Path $cfgOld) -and ((Get-FileHash $cfgOld).Hash -ne (Get-FileHash $cfgNew).Hash)) {
        $backup = "$dest\config.{0:yyyyMMdd-HHmmss}.bak.json" -f (Get-Date)
        Copy-Item $cfgOld $backup
    }
    Copy-Item $cfgNew $cfgOld -Force

    # Program Files gives ordinary users read/execute only; make sure nothing more was inherited.
    $weak = Get-UcWritableBy $dest (Get-UcWeakSids)
    if ($weak) { throw "$dest is writable by: $($weak -join ', '). Fix its permissions before using the tool." }

    Initialize-UcDataDir
    $legacy = Remove-UcLegacyInstall

    # Desktop shortcut that always asks for elevation.
    $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'UseControl.lnk'
    $sh = New-Object -ComObject WScript.Shell
    $s = $sh.CreateShortcut($lnk)
    $s.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $s.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$dest\UseControl-GUI.ps1`""
    $s.WorkingDirectory = $dest
    $s.IconLocation = if (Test-Path "$dest\uc.ico") { "$dest\uc.ico,0" } else { "$env:SystemRoot\System32\imageres.dll,73" }
    $s.Description = 'UseControl: restrictions for standard accounts'
    $s.Save()
    $bytes = [IO.File]::ReadAllBytes($lnk)
    $bytes[0x15] = $bytes[0x15] -bor 0x20   # "Run as administrator"
    [IO.File]::WriteAllBytes($lnk, $bytes)

    [pscustomobject]@{ InstalledTo = $dest; ConfigBackup = $backup; Shortcut = $lnk; RemovedOldVersion = ($legacy -join '; ') }
}
