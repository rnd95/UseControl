# Pester 3.4 (built into Windows 10): Invoke-Pester <project>\tests
# Covers everything that does not touch the system: config handling, validation and the rule set.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here '..\lib\Config.ps1')
. (Join-Path $here '..\lib\Scan.ps1')
$sampleFile = Join-Path $here '..\config.sample.json'

function New-TestConfig { $c = Get-UcConfig $sampleFile; $c.users = @('kid'); $c }

Describe 'config.sample.json' {
    $c = Get-UcConfig $sampleFile
    It 'loads with the required sections and no accounts' {
        @($c.users).Count | Should Be 0
        @($c.blockedPaths).Count | Should BeGreaterThan 5
        @($c.blockedNames).Count | Should BeGreaterThan 10
        $c.auto.systemDriveRoot | Should Be $true
    }
    It 'uses only known categories' {
        @($c.blockedPaths + $c.blockedNames | Where-Object { $_.category -notin $UcCategories }).Count | Should Be 0
    }
    It 'has only valid and allowed paths' {
        foreach ($e in $c.blockedPaths) { Get-UcPathProblem $e.path | Should BeNullOrEmpty }
    }
    It 'has only valid program names' {
        foreach ($e in $c.blockedNames) { Test-UcProgramName $e.name | Should Be $true }
    }
    It 'has no duplicates' {
        @($c.blockedPaths | Group-Object { (ConvertTo-UcPath $_.path).ToLower() } | Where-Object Count -gt 1).Count | Should Be 0
        @($c.blockedNames | Group-Object { $_.name.ToLower() } | Where-Object Count -gt 1).Count | Should Be 0
    }
}

Describe 'config.json of this PC (if present)' {
    $local = Join-Path $here '..\config.json'
    It 'is valid' {
        if (-not (Test-Path $local)) { return }
        $c = Get-UcConfig $local
        foreach ($e in $c.blockedPaths) { Get-UcPathProblem $e.path | Should BeNullOrEmpty }
        foreach ($e in $c.blockedNames) { Test-UcProgramName $e.name | Should Be $true }
        @($c.blockedPaths + $c.blockedNames | Where-Object { $_.category -notin $UcCategories }).Count | Should Be 0
    }
}

Describe 'Older config files' {
    It 'gets defaults for missing sections' {
        $tmp = Join-Path $env:TEMP "uc-test-$([guid]::NewGuid()).json"
        try {
            [IO.File]::WriteAllText($tmp, '{ "users": ["a"], "blockedPaths": [], "blockedNames": [], "browsers": {} }')
            $c = Get-UcConfig $tmp
            $c.auto.writableDriveRoots | Should Be $true
            @($c.allowedPaths).Count | Should Be 0
            @($c.audit.admins).Count | Should Be 0
        } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
    }
}

Describe 'ConvertTo-UcPath' {
    It 'normalises drive roots' { ConvertTo-UcPath 'd:' | Should Be 'D:\'; ConvertTo-UcPath 'e:\' | Should Be 'E:\' }
    It 'removes trailing backslash and quotes' { ConvertTo-UcPath ' "F:\Games\X\" ' | Should Be 'F:\Games\X' }
    It 'expands machine environment variables' { ConvertTo-UcPath '%ProgramFiles%\X' | Should Be "$env:ProgramFiles\X" }
    It 'rejects relative paths' { { ConvertTo-UcPath 'Games\X' } | Should Throw }
    It 'rejects wildcards' { { ConvertTo-UcPath 'F:\Games\*' } | Should Throw }
}

Describe 'Get-UcPathProblem' {
    It 'refuses Windows, Program Files and the system drive' {
        Get-UcPathProblem "$env:SystemDrive\" | Should Not BeNullOrEmpty
        Get-UcPathProblem $env:SystemRoot | Should Not BeNullOrEmpty
        Get-UcPathProblem "$($env:ProgramFiles.ToLower())\" | Should Not BeNullOrEmpty
    }
    It 'allows folders inside them and other drives' {
        Get-UcPathProblem "$env:SystemRoot\Temp" | Should BeNullOrEmpty
        Get-UcPathProblem "$env:ProgramFiles\Steam" | Should BeNullOrEmpty
        Get-UcPathProblem 'D:\' | Should BeNullOrEmpty
    }
}

Describe 'Get-UcRuleSet' {
    $auto = @([pscustomobject]@{ Path = 'C:\'; Category = 'writable' }, [pscustomobject]@{ Path = 'D:\'; Category = 'writable' },
              [pscustomobject]@{ Path = 'C:\Program Files (x86)\Steam'; Category = 'writable' })
    $system = @('C:\Windows', 'C:\Program Files', 'C:\Program Files (x86)')
    $c = New-TestConfig
    $c.blockedPaths = @([pscustomobject]@{ path = 'D:\games'; category = 'games' }, [pscustomobject]@{ path = 'C:\Program Files (x86)\Steam'; category = 'games' },
                        [pscustomobject]@{ path = 'C:\Program Files\WireGuard'; category = 'vpn' })
    $c.allowedPaths = @([pscustomobject]@{ path = 'C:\Tools'; note = '' })

    It 'blocks the profile, automatic places and the config list' {
        $r = Get-UcRuleSet $c 'C:\Users\kid' $auto $system $false
        $b = @($r.Blocked | ForEach-Object Path)
        $b[0] | Should Be 'C:\Users\kid'
        $b -contains 'D:\' | Should Be $true
        $b -contains 'D:\games' | Should Be $true
        $b -contains 'C:\Program Files\WireGuard' | Should Be $true
    }
    It 'lists each path once' {
        $r = Get-UcRuleSet $c 'C:\Users\kid' $auto $system $false
        @($r.Blocked | Where-Object Path -eq 'C:\Program Files (x86)\Steam').Count | Should Be 1
    }
    It 'keeps Windows, Program Files and the allowed folders allowed' {
        $r = Get-UcRuleSet $c 'C:\Users\kid' $auto $system $false
        $a = @($r.Allowed | ForEach-Object Path)
        $a -contains 'C:\Windows' | Should Be $true
        $a -contains 'C:\Program Files (x86)' | Should Be $true
        $a -contains 'C:\Tools' | Should Be $true
        $a -contains 'D:\games' | Should Be $false
    }
    It 'during games time allows game folders, even inside a blocked drive or writable folder' {
        $r = Get-UcRuleSet $c 'C:\Users\kid' $auto $system $true
        $b = @($r.Blocked | ForEach-Object Path); $a = @($r.Allowed | ForEach-Object Path)
        $a -contains 'D:\games' | Should Be $true
        $a -contains 'C:\Program Files (x86)\Steam' | Should Be $true
        $b -contains 'C:\Program Files (x86)\Steam' | Should Be $false
        $b -contains 'D:\' | Should Be $true
        $b -contains 'C:\Program Files\WireGuard' | Should Be $true
    }
    It 'never allows and blocks the same path' {
        foreach ($g in $false, $true) {
            $r = Get-UcRuleSet $c 'C:\Users\kid' $auto $system $g
            $b = @($r.Blocked | ForEach-Object { $_.Path.ToLower() })
            @($r.Allowed | Where-Object { $_.Path.ToLower() -in $b }).Count | Should Be 0
        }
    }
}

Describe 'Editing the lists' {
    It 'adds and removes a blocked folder' {
        $c = Add-UcBlockedPath (New-TestConfig) 'f:\Test Games\' 'games'
        ($c.blockedPaths | Where-Object path -eq 'F:\Test Games').category | Should Be 'games'
        $c = Remove-UcBlockedPath $c 'F:\Test Games'
        @($c.blockedPaths | Where-Object path -eq 'F:\Test Games').Count | Should Be 0
    }
    It 'refuses duplicates, protected folders and unknown categories' {
        $c = Add-UcBlockedPath (New-TestConfig) 'F:\X' 'games'
        { Add-UcBlockedPath $c 'F:\X\' 'games' } | Should Throw
        { Add-UcBlockedPath (New-TestConfig) $env:SystemRoot 'games' } | Should Throw
        { Add-UcBlockedPath (New-TestConfig) 'F:\X' 'nonsense' } | Should Throw
        { Remove-UcBlockedPath (New-TestConfig) 'F:\Not There' } | Should Throw
    }
    It 'adds and removes an allowed folder, refusing whole drives' {
        $c = Add-UcAllowedPath (New-TestConfig) 'C:\Tools'
        @($c.allowedPaths | Where-Object path -eq 'C:\Tools').Count | Should Be 1
        { Add-UcAllowedPath $c 'C:\Tools' } | Should Throw
        { Add-UcAllowedPath (New-TestConfig) 'D:\' } | Should Throw
        $c = Remove-UcAllowedPath $c 'C:\Tools'
        @($c.allowedPaths).Count | Should Be 0
    }
    It 'adds and removes a program name' {
        $c = Add-UcBlockedName (New-TestConfig) 'mygame.exe' 'games'
        @($c.blockedNames | Where-Object name -eq 'mygame.exe').Count | Should Be 1
        $c = Remove-UcBlockedName $c 'mygame.exe'
        @($c.blockedNames | Where-Object name -eq 'mygame.exe').Count | Should Be 0
    }
    It 'refuses names that are not program files' {
        { Add-UcBlockedName (New-TestConfig) 'C:\x\game.exe' 'games' } | Should Throw
        { Add-UcBlockedName (New-TestConfig) 'readme.txt' 'games' } | Should Throw
    }
    It 'skips categories in the name list' {
        $c = New-TestConfig
        (Get-UcBlockedNameList $c @('games')) -contains 'steam.exe' | Should Be $false
        (Get-UcBlockedNameList $c) -contains 'steam.exe' | Should Be $true
        (Get-UcBlockedNameList $c @('games')) -contains 'cmd.exe' | Should Be $true
    }
    It 'adds and removes accounts' {
        $c = Add-UcUser (New-TestConfig) 'kid2'
        $c.users -contains 'kid2' | Should Be $true
        { Add-UcUser $c 'kid2' } | Should Throw
        { Add-UcUser $c 'bad\name' } | Should Throw
        $c = Remove-UcUser $c 'kid2'
        $c.users -contains 'kid2' | Should Be $false
    }
}

Describe 'Extensions' {
    $chromeId = 'cjpalhdlnbpafiamejdnhcphjbkeiagm'
    It 'validates IDs per browser' {
        Test-UcExtensionId 'chrome' $chromeId | Should Be $true
        Test-UcExtensionId 'chrome' 'not-an-id' | Should Be $false
        Test-UcExtensionId 'firefox' 'uBlock0@raymondhill.net' | Should Be $true
        Test-UcExtensionId 'firefox' '{12345678-1234-1234-1234-123456789abc}' | Should Be $true
    }
    It 'adds, refuses a duplicate and removes' {
        $c = Add-UcExtension (New-TestConfig) 'chrome' $chromeId
        $c.browsers.chrome.extensionsAllowed -contains $chromeId | Should Be $true
        { Add-UcExtension $c 'chrome' $chromeId } | Should Throw
        $c = Remove-UcExtension $c 'chrome' $chromeId
        @($c.browsers.chrome.extensionsAllowed).Count | Should Be 0
    }
    It 'builds the Firefox policy JSON' {
        $c = Add-UcExtension (New-TestConfig) 'firefox' 'uBlock0@raymondhill.net'
        $j = Get-UcFirefoxExtensionJson $c | ConvertFrom-Json
        $j.'*'.installation_mode | Should Be 'blocked'
        $j.'uBlock0@raymondhill.net'.installation_mode | Should Be 'allowed'
    }
}

Describe 'Saving' {
    It 'round-trips the config without losing anything, Cyrillic included' {
        $tmp = Join-Path $env:TEMP "uc-test-$([guid]::NewGuid()).json"
        try {
            $c = New-TestConfig
            $c.audit.privateFolders = @('D:\' + [string][char]0x0421 + [char]0x0442 + [char]0x0430 + [char]0x0441)
            Save-UcConfig $c $tmp
            $d = Get-UcConfig $tmp
            @($d.blockedPaths).Count | Should Be @($c.blockedPaths).Count
            @($d.blockedNames).Count | Should Be @($c.blockedNames).Count
            $d.users -contains 'kid' | Should Be $true
            $d.browsers.chrome.policies.DnsOverHttpsMode | Should Be 'off'
            $d.browsers.chrome.policies.IncognitoModeAvailability | Should Be 1
            $d.audit.privateFolders[0] | Should Be $c.audit.privateFolders[0]
        } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
    }
}

Describe 'Registry values from JSON' {
    It 'maps numbers and booleans to DWORD and text to String' {
        (ConvertTo-UcRegValue 2).Type | Should Be 'DWord'
        (ConvertTo-UcRegValue $true).Data | Should Be 1
        (ConvertTo-UcRegValue 'off').Type | Should Be 'String'
    }
}

Describe 'Test-UcPathCovered' {
    It 'matches the folder and everything under it, nothing beside it' {
        Test-UcPathCovered 'D:\games\X' @('D:\') | Should Be $true
        Test-UcPathCovered 'F:\Programs\Steam' @('F:\Programs\Steam') | Should Be $true
        Test-UcPathCovered 'F:\Programs\SteamTools' @('F:\Programs\Steam') | Should Be $false
    }
}
