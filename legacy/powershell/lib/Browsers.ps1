# Browser policies for a restricted account: written under its own HKCU\Software\Policies, which the
# restrictions make read-only for that user.

$script:UcBrowserKeys = @{
    chrome  = 'Software\Policies\Google\Chrome'
    edge    = 'Software\Policies\Microsoft\Edge'
    firefox = 'Software\Policies\Mozilla\Firefox'
}

function Remove-UcBrowserPolicies([string]$Root) {
    foreach ($k in $script:UcBrowserKeys.Values) { Remove-Item -Path "$Root\$k" -Recurse -ErrorAction SilentlyContinue }
}

function Set-UcBrowserPolicies([string]$Root, $Config) {
    foreach ($b in 'chrome', 'edge') {
        $bc = $Config.browsers.$b
        if (-not $bc) { continue }
        $key = "$Root\$($script:UcBrowserKeys[$b])"
        Confirm-Key $key
        if ($bc.policies) {
            foreach ($p in $bc.policies.PSObject.Properties) {
                if ($null -eq $p.Value) { continue }   # null in config.json = policy not set
                $v = ConvertTo-UcRegValue $p.Value
                New-ItemProperty -Path $key -Name $p.Name -PropertyType $v.Type -Value $v.Data -Force | Out-Null
            }
        }
        if ($bc.blockExtensions) {
            Confirm-Key "$key\ExtensionInstallBlocklist"
            New-ItemProperty -Path "$key\ExtensionInstallBlocklist" -Name '1' -PropertyType String -Value '*' -Force | Out-Null
            if ($bc.extensionsAllowed) {
                Confirm-Key "$key\ExtensionInstallAllowlist"
                $i = 1
                foreach ($id in $bc.extensionsAllowed) { New-ItemProperty -Path "$key\ExtensionInstallAllowlist" -Name "$i" -PropertyType String -Value $id -Force | Out-Null; $i++ }
            }
        }
    }

    $fc = $Config.browsers.firefox
    if ($fc) {
        $key = "$Root\$($script:UcBrowserKeys.firefox)"
        Confirm-Key $key
        if ($fc.blockExtensions) {
            New-ItemProperty -Path $key -Name ExtensionSettings -PropertyType MultiString -Value @(Get-UcFirefoxExtensionJson $Config) -Force | Out-Null
        }
        if ($fc.disableDnsOverHttps) {
            Confirm-Key "$key\DNSOverHTTPS"
            New-ItemProperty -Path "$key\DNSOverHTTPS" -Name Enabled -PropertyType DWord -Value 0 -Force | Out-Null
            New-ItemProperty -Path "$key\DNSOverHTTPS" -Name Locked -PropertyType DWord -Value 1 -Force | Out-Null
        }
        if ($fc.disablePrivateBrowsing) {
            New-ItemProperty -Path $key -Name DisablePrivateBrowsing -PropertyType DWord -Value 1 -Force | Out-Null
        }
    }
}
