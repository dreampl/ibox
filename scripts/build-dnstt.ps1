<#
.SYNOPSIS
  Builds the dnstt DNS tunnel client from source (dnstt publishes no binaries).

.DESCRIPTION
  - The Go toolchain is downloaded from go.dev and checked against the SHA-256 pinned in
    cores/cores.lock.json ("build.goSha256"), then kept in .cache/go.
  - Go verifies every module against the public checksum database (sum.golang.org).
  - The build is reproducible (-trimpath, no build id), so the same version always gives the same file.
  - Works on Windows PowerShell 5.1 and PowerShell 7+.

.PARAMETER OutDir
  Folder the binaries are written to: <OutDir>/<goos>-<goarch>/dnstt-client[.exe].

.PARAMETER Targets
  goos/goarch pairs, e.g. windows/amd64, linux/amd64, linux/arm64, android/arm64.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string[]]$Targets = @('windows/amd64')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot
$lock = Get-Content -Raw -Path (Join-Path $root 'cores\cores.lock.json') | ConvertFrom-Json
$entry = @($lock.cores | Where-Object { $_.id -eq 'dnstt' })[0]
if ($null -eq $entry) { throw 'cores.lock.json has no dnstt entry.' }
$b = $entry.build

$cache = Join-Path $root '.cache'
$goRoot = Join-Path $cache "go-$($b.go)"
$goExe = Join-Path $goRoot 'go\bin\go.exe'
if (-not (Test-Path $goExe)) {
    New-Item -ItemType Directory -Force -Path $goRoot | Out-Null
    $zip = Join-Path $cache "go$($b.go).windows-amd64.zip"
    if (-not (Test-Path $zip) -or (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant() -ne $b.goSha256) {
        Write-Host "    downloading Go $($b.go)"
        Invoke-WebRequest -UseBasicParsing -Uri "https://go.dev/dl/go$($b.go).windows-amd64.zip" -OutFile "$zip.partial"
        $actual = (Get-FileHash -Algorithm SHA256 "$zip.partial").Hash.ToLowerInvariant()
        if ($actual -ne $b.goSha256) {
            Remove-Item -Force "$zip.partial"
            throw "Go archive SHA-256 mismatch: expected $($b.goSha256), got $actual."
        }
        Move-Item -Force "$zip.partial" $zip
    }
    Expand-Archive -Path $zip -DestinationPath $goRoot -Force
}

$work = Join-Path $cache 'dnstt-build'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$env:GOPATH = Join-Path $cache 'gopath'
$env:GOFLAGS = '-modcacherw -mod=mod'
$env:GOTOOLCHAIN = 'local'
$env:CGO_ENABLED = '0'
$env:GOSUMDB = 'sum.golang.org'

# Go reports progress on stderr; Windows PowerShell 5.1 would turn that into a terminating error.
function Invoke-Go {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $goExe @args 2>&1 | ForEach-Object { "$_" } | Write-Host }
    finally { $ErrorActionPreference = $old }
    if ($LASTEXITCODE -ne 0) { throw "go $($args -join ' ') failed." }
}

Push-Location $work
try {
    if (-not (Test-Path 'go.mod')) { Invoke-Go mod init dnsttbuild }
    Invoke-Go get "$($b.package)@v$($entry.version)"

    foreach ($t in $Targets) {
        $parts = $t.Split('/')
        $env:GOOS = $parts[0]
        $env:GOARCH = $parts[1]
        if ($parts[1] -eq 'arm') { $env:GOARM = '7' } else { Remove-Item Env:GOARM -ErrorAction SilentlyContinue }
        $name = if ($parts[0] -eq 'windows') { 'dnstt-client.exe' } else { 'dnstt-client' }
        $dir = Join-Path $OutDir "$($parts[0])-$($parts[1])"
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Invoke-Go build -trimpath -ldflags '-s -w -buildid=' -o (Join-Path $dir $name) $b.package
        $hash = (Get-FileHash -Algorithm SHA256 (Join-Path $dir $name)).Hash.ToLowerInvariant()
        Write-Host "    built $t -> $name ($hash)"
        # The build is reproducible: a pinned target must come out bit-for-bit the same.
        $key = "$($parts[0])-$($parts[1])"
        if (($b.PSObject.Properties.Name -contains 'outputs') -and ($b.outputs.PSObject.Properties.Name -contains $key) -and $b.outputs.$key -ne $hash) {
            throw "dnstt-client for $t does not match the pinned SHA-256 $($b.outputs.$key). Refusing to use it."
        }
    }

    $license = Join-Path $env:GOPATH "pkg\mod\$($b.module)@v$($entry.version)\COPYING"
    if (Test-Path $license) { Copy-Item -Force $license (Join-Path $OutDir 'COPYING') }
}
finally {
    Remove-Item Env:GOOS, Env:GOARCH, Env:GOARM -ErrorAction SilentlyContinue
    Pop-Location
}
