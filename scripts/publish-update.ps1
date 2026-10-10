<#
.SYNOPSIS
  Builds the signed app-update feed for a release that scripts\package.ps1 already produced.

.DESCRIPTION
  Output: artifacts\update-site\ — upload its CONTENTS to https://connect.jackiairline.ir/update/ :
      latest.json, latest.json.sig          (the feed; replace them on every release)
      <version>\JackiVpn-<version>-*.msi/.apk (the installers; keep old version folders or delete them)
  Apps check latest.json every few hours, verify the signature with the public key built into them, and only
  accept installers whose SHA-256 and size match the signed feed.

  Release notes (optional, UTF-8): release-notes\<version>.en.txt and release-notes\<version>.fa.txt
  Signing key: %USERPROFILE%\.jackivpn\update-signing-key.pem (back it up; without it no update can be published).

.PARAMETER Mandatory
  Marks the update as required (the app keeps offering it even if the user chose Skip).
#>
param([switch]$Mandatory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$release = Join-Path $root "artifacts\release\$version"
$site = Join-Path $root 'artifacts\update-site'
$key = Join-Path $env:USERPROFILE '.jackivpn\update-signing-key.pem'
if (-not (Test-Path $release)) { throw "Run scripts\package.ps1 first ($release is missing)." }
if (-not (Test-Path $key)) { throw "Update signing key not found: $key" }

$target = Join-Path $site $version
New-Item -ItemType Directory -Force $target | Out-Null
Get-ChildItem $release -File | Where-Object { $_.Extension -in '.msi', '.apk' } | Copy-Item -Destination $target -Force

$cli = @('run', '--project', 'src\JackiVpn.Service\JackiVpn.Service.csproj', '-c', 'Release', '--', 'update-feed',
    '--release', $release, '--version', $version, '--key', $key, '--out', $site,
    '--base-url', 'https://connect.jackiairline.ir/update')
foreach ($lang in 'en', 'fa') {
    $notes = Join-Path $root "release-notes\$version.$lang.txt"
    if (Test-Path $notes) { $cli += @("--notes-$lang", [IO.File]::ReadAllText($notes, [Text.Encoding]::UTF8).Trim()) }
}
if ($Mandatory) { $cli += '--mandatory' }
& dotnet @cli
if ($LASTEXITCODE -ne 0) { throw 'update-feed failed.' }

Write-Host ''
Write-Host "Upload the contents of $site to https://connect.jackiairline.ir/update/" -ForegroundColor Green
Get-ChildItem $site -Recurse -File | ForEach-Object { '  ' + $_.FullName.Substring($site.Length + 1) }