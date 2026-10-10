<#
.SYNOPSIS
  Downloads the pinned Android binaries (cores\android.lock.json), verifies SHA-256 and places them in the
  Android project: Platforms\Android\libs\<abi>\libsingbox.so and Platforms\Android\Jars\hev-socks5-tunnel.aar.
#>
[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$lock = Get-Content (Join-Path $root 'cores\android.lock.json') -Raw | ConvertFrom-Json
$android = Join-Path $root 'src\JackiVpn.Mobile\Platforms\Android'
$cache = Join-Path $root 'cores\download'
New-Item -ItemType Directory -Force $cache | Out-Null

function Get-Verified([string]$url, [string]$sha256) {
    $file = Join-Path $cache ([IO.Path]::GetFileName(([Uri]$url).AbsolutePath))
    if ($Force -or -not (Test-Path $file) -or ((Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant() -ne $sha256)) {
        Write-Host "Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing
    }
    $actual = (Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant()
    if ($actual -ne $sha256) { Remove-Item $file -Force; throw "SHA-256 mismatch for $url (expected $sha256, got $actual)" }
    return $file
}

foreach ($item in $lock.items) {
    if ($item.id -eq 'sing-box') {
        foreach ($abi in $item.abis.PSObject.Properties) {
            $archive = Get-Verified $abi.Value.url $abi.Value.sha256
            $tmp = Join-Path $cache ("x-" + $abi.Name)
            if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
            New-Item -ItemType Directory -Force $tmp | Out-Null
            tar -xzf $archive -C $tmp
            if ($LASTEXITCODE -ne 0) { throw "tar failed for $archive" }
            $bin = Get-ChildItem $tmp -Recurse -File | Where-Object { $_.Name -eq 'sing-box' } | Select-Object -First 1
            if (-not $bin) { throw "sing-box binary not found in $archive" }
            $dest = Join-Path $android "libs\$($abi.Name)"
            New-Item -ItemType Directory -Force $dest | Out-Null
            Copy-Item $bin.FullName (Join-Path $dest 'libsingbox.so') -Force
            $license = Get-ChildItem $tmp -Recurse -File | Where-Object { $_.Name -eq 'LICENSE' } | Select-Object -First 1
            if ($license) { Copy-Item $license.FullName (Join-Path $android 'Assets\licenses\sing-box-LICENSE.txt') -Force -ErrorAction SilentlyContinue }
            Remove-Item $tmp -Recurse -Force
            Write-Host "sing-box $($item.version) -> libs\$($abi.Name)\libsingbox.so"
        }
    }
    elseif ($item.id -eq 'hev-socks5-tunnel') {
        $aar = Get-Verified $item.aar.url $item.aar.sha256
        $dest = Join-Path $android 'Jars'
        New-Item -ItemType Directory -Force $dest | Out-Null
        Copy-Item $aar (Join-Path $dest 'hev-socks5-tunnel.aar') -Force
        Write-Host "hev-socks5-tunnel $($item.version) -> Jars\hev-socks5-tunnel.aar"
    }
}
Write-Host 'Android binaries ready.'
