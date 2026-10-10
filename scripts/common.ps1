# Shared helpers for the build/test scripts (dot-source this file).

function Initialize-Dotnet {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'

    # Prefer a user-local SDK (dotnet-install.ps1 default); a machine-wide "dotnet" may be runtime-only.
    $local = Join-Path $env:USERPROFILE '.dotnet'
    if (Get-ChildItem (Join-Path $local 'sdk') -Directory -Filter '10.*' -ErrorAction SilentlyContinue) {
        $env:DOTNET_ROOT = $local
        $env:PATH = "$local;$env:PATH"
        return
    }

    $sdks = if (Get-Command dotnet -ErrorAction SilentlyContinue) { & dotnet --list-sdks 2>$null } else { @() }
    if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
        throw '.NET SDK 10 not found. Install it from https://dot.net (or scripts\install-sdk.ps1 into %USERPROFILE%\.dotnet).'
    }
}

function Write-Step([string]$Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
