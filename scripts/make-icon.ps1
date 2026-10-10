<#
.SYNOPSIS
  Regenerates src/JackiVpn.Desktop/Assets/jacki.ico (multi-size) and a 512 px PNG preview next to it from the
  IBOX vector logo (assets/brand). Wordmark from 40 px up; the "O" box with its blue pixel below that.
#>
[CmdletBinding()]
param([string]$Output)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Output) { $Output = Join-Path $root '..\src\JackiVpn.Desktop\Assets\jacki.ico' }
. (Join-Path $root 'common.ps1')
Initialize-Dotnet
& dotnet run (Join-Path $root 'make-icon.cs') -- ([IO.Path]::GetFullPath($Output))
if ($LASTEXITCODE -ne 0) { throw "make-icon.cs failed ($LASTEXITCODE)" }