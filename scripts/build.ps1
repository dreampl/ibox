<#
.SYNOPSIS
  Builds the solution and publishes a runnable IBOX folder (app + service + verified cores).

.DESCRIPTION
  Output layout (default artifacts\JackiVpn):
    JackiVpn.exe            desktop app (runs as the user)
    JackiVpn.Service.exe    privileged service (install with: JackiVpn.Service.exe install, as admin)
    cores\                  sing-box, Xray, Wintun + manifest.json (SHA-256 per file)
  For real use copy the folder to a location only administrators can modify (e.g. C:\Program Files\JackiVpn);
  the service installer refuses user-writable locations unless --dev is given.

.PARAMETER SelfContained
  Bundle the .NET runtime (no .NET 10 Desktop Runtime needed on the target machine). Larger output.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Release',
    [string]$Output,
    [switch]$SelfContained,
    [switch]$SkipCores,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
. (Join-Path $root 'scripts\common.ps1')
Initialize-Dotnet
if (-not $Output) {
    $version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $Output = Join-Path $root "artifacts\JackiVpn-$version"
}

# Never clean or overwrite a folder the installed service runs from: its files are locked and a partial
# delete would break a running VPN. Publish elsewhere and use "Install / Repair service" to move over.
$svc = Get-CimInstance Win32_Service -Filter "Name='JackiVpnService'" -ErrorAction SilentlyContinue
if ($svc -and $svc.PathName) {
    $svcDir = Split-Path -Parent ($svc.PathName.Trim('"'))
    if ((Test-Path $Output) -and ([IO.Path]::GetFullPath($svcDir).TrimEnd('\') -ieq [IO.Path]::GetFullPath($Output).TrimEnd('\'))) {
        throw "The installed IBOX service runs from $Output. Publish to another folder (-Output) and repair the service from the new app."
    }
}

Push-Location $root
try {
    if (-not $SkipCores -and -not (Test-Path 'cores\bin\manifest.json')) {
        Write-Step 'Fetching pinned cores (cores\cores.lock.json)'
        & (Join-Path $root 'scripts\fetch-cores.ps1')
    }

    Write-Step "Building JackiVpn.sln ($Configuration)"
    Invoke-Dotnet build JackiVpn.sln -c $Configuration --nologo

    if (-not $SkipTests) {
        Write-Step 'Unit tests'
        Invoke-Dotnet test tests\JackiVpn.UnitTests -c $Configuration --no-build --nologo
    }

    if (Test-Path $Output) { Remove-Item -Recurse -Force $Output }
    $sc = if ($SelfContained) { 'true' } else { 'false' }
    foreach ($project in 'src\JackiVpn.Desktop\JackiVpn.Desktop.csproj', 'src\JackiVpn.Service\JackiVpn.Service.csproj') {
        Write-Step "Publishing $project"
        Invoke-Dotnet publish $project -c $Configuration -r win-x64 --self-contained $sc -o $Output --nologo
    }

    # Integrity check of the shipped cores against their manifest.
    $manifest = Join-Path $Output 'cores\manifest.json'
    if (-not (Test-Path $manifest)) { throw "cores\manifest.json missing in $Output (run scripts\fetch-cores.ps1)." }
    $m = Get-Content $manifest -Raw | ConvertFrom-Json
    foreach ($core in $m.cores.PSObject.Properties) {
        foreach ($file in $core.Value.files) {
            $path = Join-Path (Join-Path $Output 'cores') $file.path
            $hash = (Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant()
            if ($hash -ne $file.sha256) { throw "Hash mismatch for $($file.path)" }
        }
    }

    Write-Step 'Done'
    Write-Host "Output: $Output"
    Write-Host 'Run:    JackiVpn.exe'
    Write-Host 'TUN:    install the service from the app (Network > Install / Repair service) after moving the folder to Program Files.'
}
finally {
    Pop-Location
}
