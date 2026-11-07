<#
.SYNOPSIS
  Builds the three UseControl programs into one folder, ready for "uc install".

.DESCRIPTION
  The service (UseControl.Service.exe), the admin panel (UseControl.exe) and the command line (uc.exe)
  are published self-contained for 64-bit Windows: the target PC needs no .NET installed.

.EXAMPLE
  .\build\publish.ps1
  .\artifacts\UseControl\uc.exe install        # in an administrator console
#>
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\UseControl'),
    # "1.2.3": the version written into the programs (default: the one in Directory.Build.props).
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Force $Output | Out-Null

$versionArgs = @()
if ($Version) { $versionArgs += "-p:Version=$Version" }
foreach ($project in 'UseControl.Service', 'UseControl.Cli', 'UseControl.Admin') {
    Write-Host "Publishing $project..." -ForegroundColor Cyan
    dotnet publish (Join-Path $root "src\$project") -c $Configuration -r $Runtime --self-contained true -o $Output -p:DebugType=none @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

$size = (Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Done: {0} ({1:N0} MB). Install: run '{2}\uc.exe install' as administrator." -f (Resolve-Path $Output), $size, (Resolve-Path $Output)) -ForegroundColor Green
