<#
.SYNOPSIS
  Makes the files of a release: the installer, a ZIP of the programs and their checksums.

.DESCRIPTION
  Run after build\publish.ps1 with the same version. Writes into artifacts\release:
    UseControl-<version>-x64.msi   the installer (what the service downloads to update itself)
    UseControl-<version>-x64.zip   the published programs, for "uc install"
    SHA256SUMS.txt                 SHA-256 of both; the service refuses an installer that does not match

.EXAMPLE
  .\build\publish.ps1 -Version 0.2.0
  .\build\package.ps1 -Version 0.2.0
#>
param(
    [Parameter(Mandatory)] [string]$Version,
    [string]$PublishDir = (Join-Path $PSScriptRoot '..\artifacts\UseControl'),
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\release')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "The version must look like 1.2.3 (got '$Version')." }
$PublishDir = (Resolve-Path $PublishDir).Path
foreach ($exe in 'UseControl.Service.exe', 'UseControl.exe', 'uc.exe') {
    if (-not (Test-Path (Join-Path $PublishDir $exe))) { throw "$exe is missing in ${PublishDir}: run build\publish.ps1 first." }
}
$built = (Get-Item (Join-Path $PublishDir 'UseControl.Service.exe')).VersionInfo.FileVersion
if (-not $built.StartsWith($Version)) { throw "The published programs are version ${built}, not ${Version}: run build\publish.ps1 -Version $Version." }

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Force $Output | Out-Null
$Output = (Resolve-Path $Output).Path
$name = "UseControl-$Version-x64"

Write-Host 'Building the installer...' -ForegroundColor Cyan
$installer = Join-Path $root 'installer\UseControl.Installer.wixproj'
dotnet build $installer -c Release "-p:Version=$Version" "-p:PublishDir=$PublishDir" "-p:OutputPath=$Output/msi/"
if ($LASTEXITCODE -ne 0) { throw 'Building the installer failed.' }
Move-Item (Join-Path $Output 'msi\UseControl.msi') (Join-Path $Output "$name.msi")
Remove-Item (Join-Path $Output 'msi') -Recurse -Force

Write-Host 'Packing the programs...' -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $PublishDir '*') -DestinationPath (Join-Path $Output "$name.zip") -CompressionLevel Optimal

# "hash  name" lines, the format of sha256sum.
$sums = Get-ChildItem $Output -File | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
[IO.File]::WriteAllText((Join-Path $Output 'SHA256SUMS.txt'), ($sums -join "`n") + "`n")

Get-ChildItem $Output -File | ForEach-Object { Write-Host ('  {0,-32} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB)) }
Write-Host "Done: $Output" -ForegroundColor Green
