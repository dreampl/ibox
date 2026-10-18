<#
.SYNOPSIS
  Builds IBAR's release only: the per-user installer, the browser extension packages, the Adobe bridge and the docs.

.DESCRIPTION
  Output (artifacts\release\ibar-<version>):
    IBAR-<v>-win-x64.msi                     per-user installer (no UAC): IBAR, ibar-ctl, ibar-host (native host)
    ibar-downloads-chromium-<v>.zip          the extension for Chrome / Edge / Opera / Brave (load unpacked)
    ibar-downloads-firefox-<v>.xpi           the extension for Firefox (unsigned)
    ibar-adobe-bridge-<v>.zip                the CEP bridge for Adobe apps (experimental, unsigned)
    INSTALL-IBAR-EN.txt, INSTALL-IBAR-FA.txt, IBAR-API.md, SHA256SUMS.txt
  IBOX, Linux and Android are not built here.

.PARAMETER Version  IBAR's version (default 2.1.0).
#>
[CmdletBinding()]
param([string]$Version = '2.1.0', [switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
. (Join-Path $root 'scripts\common.ps1')
Initialize-Dotnet
Add-Type -AssemblyName System.IO.Compression.FileSystem

$release = Join-Path $root "artifacts\release\ibar-$Version"
$publish = Join-Path $root "artifacts\publish\ibar-$Version"
$payload = Join-Path $publish 'payload'
foreach ($dir in $payload, $release) { if (Test-Path $dir) { [IO.Directory]::Delete($dir, $true) } }
New-Item -ItemType Directory -Force $payload, $release | Out-Null

function New-Zip([string]$From, [string]$To) {
    if (Test-Path $To) { [IO.File]::Delete($To) }
    # Forward slashes inside (Firefox refuses an .xpi with backslash paths).
    [IO.Compression.ZipFile]::CreateFromDirectory($From, $To, [IO.Compression.CompressionLevel]::Optimal, $false)
}

Push-Location $root
try {
    if (-not $SkipTests) {
        Write-Step 'Unit tests'
        Invoke-Dotnet test tests\JackiVpn.UnitTests -c Release --nologo
    }

    foreach ($project in 'src\JackiVpn.Bar\JackiVpn.Bar.csproj', 'src\JackiVpn.BarCtl\JackiVpn.BarCtl.csproj', 'src\JackiVpn.BarHost\JackiVpn.BarHost.csproj') {
        Write-Step "Publishing $project"
        Invoke-Dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true "-p:Version=$Version" -o $payload --nologo
    }

    Write-Step 'Native host manifests and extensions'
    $ext = Join-Path $root 'extensions\ibar-downloads'
    Copy-Item (Join-Path $ext 'host\com.jacki.ibar.json'), (Join-Path $ext 'host\com.jacki.ibar.firefox.json') $payload
    $stage = Join-Path $publish 'stage'
    if (Test-Path $stage) { [IO.Directory]::Delete($stage, $true) }
    foreach ($browser in 'chromium', 'firefox') {
        $dir = Join-Path $stage $browser
        New-Item -ItemType Directory -Force (Join-Path $dir 'icons') | Out-Null
        Copy-Item (Join-Path $ext "$browser\manifest.json"), (Join-Path $ext 'background.js') $dir
        Copy-Item (Join-Path $ext 'icons\*.png') (Join-Path $dir 'icons')
    }

    $extOut = Join-Path $payload 'extensions'
    New-Item -ItemType Directory -Force $extOut | Out-Null
    Copy-Item (Join-Path $stage 'chromium') (Join-Path $extOut 'chromium') -Recurse
    New-Zip (Join-Path $stage 'chromium') (Join-Path $release "ibar-downloads-chromium-$Version.zip")
    New-Zip (Join-Path $stage 'firefox') (Join-Path $release "ibar-downloads-firefox-$Version.xpi")
    Copy-Item (Join-Path $release "ibar-downloads-firefox-$Version.xpi") (Join-Path $extOut 'ibar-downloads-firefox.xpi')
    New-Zip (Join-Path $root 'extensions\ibar-adobe') (Join-Path $release "ibar-adobe-bridge-$Version.zip")
    Copy-Item (Join-Path $root 'extensions\ibar-adobe') (Join-Path $extOut 'ibar-adobe') -Recurse

    Write-Step 'Building the IBAR MSI (per-user)'
    Invoke-Dotnet build installer\ibar\IBAR.Installer.wixproj -c Release --nologo "-p:PayloadDir=$payload" "-p:ProductVersion=$Version" -o (Join-Path $publish 'msi')
    Copy-Item (Join-Path $publish "msi\IBAR-$Version-win-x64.msi") $release

    Write-Step 'Docs'
    Copy-Item (Join-Path $root 'installer\ibar\INSTALL-IBAR-EN.txt'), (Join-Path $root 'installer\ibar\INSTALL-IBAR-FA.txt') $release
    Copy-Item (Join-Path $root 'sdk\ibar\IBAR-API.md') $release

    $sums = Get-ChildItem $release -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant(), $_.Name
    }
    [IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'), $sums)
    Get-ChildItem $release -File | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
}
finally {
    Pop-Location
}
