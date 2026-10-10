<#
.SYNOPSIS
  Builds the Ubuntu/Debian package: artifacts\release\<version>\jackivpn_<version>_amd64.deb (+ .tar.gz).

.DESCRIPTION
  Self-contained linux-x64 CLI (no .NET install needed) with sing-box and Xray. The cores are downloaded from
  their official GitHub releases and verified (sing-box: GitHub's SHA-256 asset digest; Xray: its .dgst file);
  their hashes go into cores\manifest.json, which the app checks before every launch.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$singBoxVersion = '1.14.2'
$xrayVersion = '26.3.27'
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$work = Join-Path $root "artifacts\publish\$version\linux"
$stage = Join-Path $work 'pkg'
$app = Join-Path $stage 'opt\jackivpn'
$dl = Join-Path $root 'cores\linux-download'
if (Test-Path $work) { [IO.Directory]::Delete($work, $true) }
New-Item -ItemType Directory -Force $app, $dl | Out-Null

function Get-Verified($url, $file, $sha256) {
    if (-not (Test-Path $file) -or (Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant() -ne $sha256) {
        Invoke-WebRequest -UseBasicParsing $url -OutFile $file
    }
    $actual = (Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant()
    if ($actual -ne $sha256) { throw "Hash mismatch for $url ($actual)" }
}

Write-Step "sing-box $singBoxVersion (linux-amd64)"
$sbName = "sing-box-$singBoxVersion-linux-amd64.tar.gz"
$release = Invoke-RestMethod -UseBasicParsing "https://api.github.com/repos/SagerNet/sing-box/releases/tags/v$singBoxVersion"
$asset = $release.assets | Where-Object name -eq $sbName
if (-not $asset.digest) { throw "No published digest for $sbName" }
$sbArchive = Join-Path $dl $sbName
Get-Verified $asset.browser_download_url $sbArchive ($asset.digest -replace '^sha256:', '')
tar.exe -xzf $sbArchive -C $dl
if ($LASTEXITCODE -ne 0) { throw 'tar failed' }

Write-Step "Xray $xrayVersion (linux-64)"
$xrUrl = "https://github.com/XTLS/Xray-core/releases/download/v$xrayVersion/Xray-linux-64.zip"
$dgst = (Invoke-WebRequest -UseBasicParsing "$xrUrl.dgst").Content
if ($dgst -is [byte[]]) { $dgst = [Text.Encoding]::ASCII.GetString($dgst) }
$xrSha = ([regex]::Match($dgst, 'SHA2-256=\s*([0-9a-f]{64})')).Groups[1].Value
if (-not $xrSha) { throw 'No SHA2-256 in the Xray .dgst file' }
$xrArchive = Join-Path $dl "Xray-$xrayVersion-linux-64.zip"
Get-Verified $xrUrl $xrArchive $xrSha
$xrDir = Join-Path $dl "xray-$xrayVersion"
if (Test-Path $xrDir) { [IO.Directory]::Delete($xrDir, $true) }
Expand-Archive $xrArchive $xrDir

Write-Step 'Publishing jackivpn (self-contained linux-x64)'
Invoke-Dotnet publish src\JackiVpn.Linux\JackiVpn.Linux.csproj -c Release --nologo -o $app
Get-ChildItem $app -Filter *.pdb | ForEach-Object { [IO.File]::Delete($_.FullName) }

$cores = Join-Path $app 'cores'
New-Item -ItemType Directory -Force $cores | Out-Null
Copy-Item (Join-Path $dl "sing-box-$singBoxVersion-linux-amd64\sing-box") $cores
Copy-Item (Join-Path $xrDir 'xray') $cores
foreach ($geo in 'geoip.dat', 'geosite.dat') { if (Test-Path (Join-Path $xrDir $geo)) { Copy-Item (Join-Path $xrDir $geo) $cores } }
function Entry($path) { $f = Get-Item $path; [ordered]@{ path = $f.Name; sha256 = (Get-FileHash -Algorithm SHA256 $f.FullName).Hash.ToLowerInvariant(); size = $f.Length } }
$manifest = [ordered]@{
    schemaVersion = 1
    cores = [ordered]@{
        'sing-box' = [ordered]@{ version = $singBoxVersion; license = 'GPL-3.0-or-later'; source = 'https://github.com/SagerNet/sing-box'; files = @(Entry (Join-Path $cores 'sing-box')) }
        'xray' = [ordered]@{ version = $xrayVersion; license = 'MPL-2.0'; source = 'https://github.com/XTLS/Xray-core'; files = @(Entry (Join-Path $cores 'xray')) }
    }
}
[IO.File]::WriteAllText((Join-Path $cores 'manifest.json'), ($manifest | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
Copy-Item THIRD_PARTY_NOTICES.txt $app

Write-Step 'Building the .deb'
$out = Join-Path $root "artifacts\release\$version"
Invoke-Dotnet run scripts\make-deb.cs -- $stage $version $out
Get-ChildItem $out | Where-Object { $_.Name -clike 'jackivpn*' } | ForEach-Object { '{0,-44} {1,8:N1} MB  {2}' -f $_.Name, ($_.Length / 1MB), (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant() }
