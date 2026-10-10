<#
.SYNOPSIS
  Downloads the pinned core binaries (sing-box, Xray, Wintun), verifies them and
  writes cores/bin/manifest.json with per-file SHA-256 hashes.

.DESCRIPTION
  - Every archive is verified against the SHA-256 pinned in cores/cores.lock.json.
    A mismatch aborts the script and nothing is extracted.
  - Files marked "authenticode": true must carry a valid Authenticode signature
    whose subject contains the core's "authenticodeSubject".
  - The generated manifest is what JackiVpn verifies before launching a core.
  - Works on Windows PowerShell 5.1 and PowerShell 7+.

.PARAMETER Force
  Re-download archives even if a verified copy exists in the cache.
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot
$lockPath = Join-Path $root 'cores\cores.lock.json'
$binDir = Join-Path $root 'cores\bin'
$cacheDir = Join-Path $root '.cache\cores'

if (-not (Test-Path $lockPath)) { throw "Lock file not found: $lockPath" }
$lock = Get-Content -Raw -Path $lockPath | ConvertFrom-Json

New-Item -ItemType Directory -Force -Path $binDir, $cacheDir, (Join-Path $binDir 'licenses') | Out-Null

function Get-Sha256([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant()
}

function Get-RelativeUnixPath([string]$Base, [string]$Full) {
    $b = (Resolve-Path $Base).Path.TrimEnd('\') + '\'
    return $Full.Substring($b.Length).Replace('\', '/')
}

$manifestCores = [ordered]@{}

foreach ($core in $lock.cores) {
    Write-Host "==> $($core.id) $($core.version)" -ForegroundColor Cyan
    $fileName = [IO.Path]::GetFileName(([Uri]$core.url).AbsolutePath)
    $archive = Join-Path $cacheDir "$($core.id)-$($core.version)-$fileName"
    $expected = $core.sha256.ToLowerInvariant()

    $needDownload = $Force -or -not (Test-Path $archive) -or ((Get-Sha256 $archive) -ne $expected)
    if ($needDownload) {
        Write-Host "    downloading $($core.url)"
        $tmp = "$archive.partial"
        if (Test-Path $tmp) { Remove-Item -Force $tmp }
        Invoke-WebRequest -UseBasicParsing -Uri $core.url -OutFile $tmp -Headers @{ 'User-Agent' = 'jackivpn-build' }
        $actual = Get-Sha256 $tmp
        if ($actual -ne $expected) {
            Remove-Item -Force $tmp
            throw "SHA-256 mismatch for $($core.id): expected $expected, got $actual. Refusing to use the archive."
        }
        Move-Item -Force $tmp $archive
    }
    Write-Host "    archive sha256 OK ($expected)"

    $extract = Join-Path $cacheDir "extract-$($core.id)"
    if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
    Expand-Archive -Path $archive -DestinationPath $extract -Force

    $all = Get-ChildItem -Path $extract -Recurse -File
    $fileEntries = @()
    foreach ($spec in $core.files) {
        $optional = ($spec.PSObject.Properties.Name -contains 'optional') -and $spec.optional
        $found = @($all | Where-Object { (Get-RelativeUnixPath $extract $_.FullName) -like $spec.match })
        if ($found.Count -eq 0) {
            if ($optional) { continue }
            throw "File pattern '$($spec.match)' not found in $($core.id) archive."
        }
        foreach ($m in $found) {
            if ($spec.target -eq '.') { $targetRel = $m.Name } else { $targetRel = $spec.target }
            $target = Join-Path $binDir $targetRel
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null

            if (($spec.PSObject.Properties.Name -contains 'authenticode') -and $spec.authenticode) {
                $sig = Get-AuthenticodeSignature -FilePath $m.FullName
                if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike "*$($core.authenticodeSubject)*") {
                    throw "Authenticode check failed for $($m.Name): status=$($sig.Status) subject=$($sig.SignerCertificate.Subject)"
                }
                Write-Host "    authenticode OK: $($sig.SignerCertificate.Subject)"
            }

            Copy-Item -Force -Path $m.FullName -Destination $target
            $fileEntries += [ordered]@{
                path   = $targetRel.Replace('\', '/')
                sha256 = Get-Sha256 $target
                size   = (Get-Item $target).Length
            }
            Write-Host "    -> $targetRel"
        }
    }
    Remove-Item -Recurse -Force $extract

    $manifestCores[$core.id] = [ordered]@{
        version       = $core.version
        archiveSha256 = $expected
        license       = $core.license
        source        = $core.source
        files         = $fileEntries
    }
}

$manifest = [ordered]@{
    schemaVersion = 1
    generatedUtc  = (Get-Date).ToUniversalTime().ToString('o')
    cores         = $manifestCores
}
$manifestPath = Join-Path $binDir 'manifest.json'
$json = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($manifestPath, $json, (New-Object Text.UTF8Encoding($false)))
Write-Host "Manifest written: $manifestPath" -ForegroundColor Green
