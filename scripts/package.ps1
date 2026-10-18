<#
.SYNOPSIS
  Builds the installable release: a Windows MSI and signed Android APKs.

.DESCRIPTION
  Output (artifacts\release\<version>):
    IBOX-<v>-win-x64.msi                 Windows 10/11 x64 installer (self-contained: no .NET install needed)
    IBOX-<v>-android-arm64-v8a.apk       almost all phones from the last ~8 years
    IBOX-<v>-android-armeabi-v7a.apk     old 32-bit phones
    IBOX-<v>-android-x86_64.apk          emulators / x86 tablets
    SHA256SUMS.txt, THIRD_PARTY_NOTICES.txt, install guides

  Android signing key: %USERPROFILE%\.jackivpn\android-release.keystore (created on first run) with its password
  in android-release.password next to it. BACK BOTH UP: Android only installs updates signed with the same key.

.PARAMETER SkipWindows / SkipAndroid / SkipTests
#>
[CmdletBinding()]
param(
    [switch]$SkipWindows,
    [switch]$SkipAndroid,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
. (Join-Path $root 'scripts\common.ps1')
Initialize-Dotnet

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$release = Join-Path $root "artifacts\release\$version"
$publish = Join-Path $root "artifacts\publish\$version"
New-Item -ItemType Directory -Force $release | Out-Null

Push-Location $root
try {
    if (-not $SkipWindows) {
        if (-not (Test-Path 'cores\bin\manifest.json')) {
            Write-Step 'Fetching pinned Windows cores'
            & (Join-Path $root 'scripts\fetch-cores.ps1')
        }

        Write-Step "Building JackiVpn.sln (Release)"
        Invoke-Dotnet build JackiVpn.sln -c Release --nologo
        if (-not $SkipTests) {
            Write-Step 'Unit tests'
            Invoke-Dotnet test tests\JackiVpn.UnitTests -c Release --no-build --nologo
        }

        $payload = Join-Path $publish 'win-x64'
        if (Test-Path $payload) { Remove-Item -Recurse -Force $payload }
        foreach ($project in 'src\JackiVpn.Desktop\JackiVpn.Desktop.csproj', 'src\JackiVpn.Service\JackiVpn.Service.csproj', 'src\JackiVpn.AgentCli\JackiVpn.AgentCli.csproj') {
            Write-Step "Publishing $project (self-contained win-x64, ReadyToRun)"
            # ReadyToRun only for the app (its memory is what people see); the service stays as it was in 2.0.0:
            # it identifies its clients under impersonation, where late-loaded precompiled code cannot be read.
            $r2r = if ($project -like "*Desktop*") { "-p:PublishReadyToRun=true" } else { "-p:PublishReadyToRun=false" }
            Invoke-Dotnet publish $project -c Release -r win-x64 --self-contained true $r2r -o $payload --nologo
        }

        Write-Step 'Verifying shipped cores against their manifest'
        $m = Get-Content (Join-Path $payload 'cores\manifest.json') -Raw | ConvertFrom-Json
        foreach ($core in $m.cores.PSObject.Properties) {
            foreach ($file in $core.Value.files) {
                $path = Join-Path (Join-Path $payload 'cores') $file.path
                if ((Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Hash mismatch for $($file.path)" }
            }
        }

        # IBAR: its own per-user installer, also shipped inside IBOX for the "Install IBAR" button.
        $ibarPayload = Join-Path $publish 'ibar-win-x64'
        if (Test-Path $ibarPayload) { Remove-Item -Recurse -Force $ibarPayload }
        foreach ($project in 'src\JackiVpn.Bar\JackiVpn.Bar.csproj', 'src\JackiVpn.BarCtl\JackiVpn.BarCtl.csproj') {
            Write-Step "Publishing $project (self-contained win-x64, ReadyToRun)"
            Invoke-Dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $ibarPayload --nologo
        }

        Write-Step 'Building the IBAR MSI (per-user)'
        Invoke-Dotnet build installer\ibar\IBAR.Installer.wixproj -c Release --nologo "-p:PayloadDir=$ibarPayload" "-p:ProductVersion=$version" -o (Join-Path $publish 'ibar-msi')
        $ibarMsi = Join-Path $publish "ibar-msi\IBAR-$version-win-x64.msi"
        Copy-Item $ibarMsi $release -Force
        New-Item -ItemType Directory -Force (Join-Path $payload 'IBAR') | Out-Null
        Copy-Item $ibarMsi (Join-Path $payload 'IBAR\IBAR-setup.msi') -Force

        Write-Step 'Building the MSI'
        Invoke-Dotnet build installer\JackiVpn.Installer.wixproj -c Release --nologo "-p:PayloadDir=$payload" "-p:ProductVersion=$version" -o (Join-Path $publish 'msi')
        Copy-Item (Join-Path $publish "msi\IBOX-$version-win-x64.msi") $release -Force
    }

    if (-not $SkipAndroid) {
        if (-not (Test-Path 'src\JackiVpn.Mobile\Platforms\Android\libs\arm64-v8a\libsingbox.so')) {
            Write-Step 'Fetching pinned Android cores'
            & (Join-Path $root 'scripts\fetch-android-cores.ps1')
        }

        $keyDir = Join-Path $env:USERPROFILE '.jackivpn'
        $keystore = Join-Path $keyDir 'android-release.keystore'
        $passwordFile = Join-Path $keyDir 'android-release.password'
        if (-not (Test-Path $keystore)) {
            Write-Step "Creating the Android release signing key ($keystore)"
            New-Item -ItemType Directory -Force $keyDir | Out-Null
            $bytes = New-Object byte[] 24
            [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
            $password = [Convert]::ToBase64String($bytes).Replace('+', 'A').Replace('/', 'B').TrimEnd('=')
            # apksigner reads the store password from line 1 and the key password from line 2 of the same file
            # (PKCS12: both are the same); keytool reads line 1.
            [IO.File]::WriteAllText($passwordFile, "$password`n$password`n")
            $keytool = Join-Path $env:USERPROFILE '.jdk\jdk21\bin\keytool.exe'
            & $keytool -genkeypair -keystore $keystore -storetype PKCS12 -alias jackivpn -keyalg RSA -keysize 4096 -validity 10000 `
                -dname 'CN=Jacki VPN, O=Jacki' -storepass:file $passwordFile -keypass:file $passwordFile
            if ($LASTEXITCODE -ne 0) { throw 'keytool failed.' }
            Write-Warning "New signing key created. Back up $keystore and $passwordFile - updates must be signed with this key."
        }

        # One APK per CPU type (each carries only its own ~75 MB sing-box), built as separate single-RID publishes.
        $apksigner = Join-Path (Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Android\Sdk\build-tools') -Directory | Sort-Object Name | Select-Object -Last 1).FullName 'lib\apksigner.jar'
        $java = Join-Path $env:USERPROFILE '.jdk\jdk21\bin\java.exe'
        foreach ($target in @(@{ Rid = 'android-arm64'; Abi = 'arm64-v8a' }, @{ Rid = 'android-arm'; Abi = 'armeabi-v7a' }, @{ Rid = 'android-x64'; Abi = 'x86_64' })) {
            Write-Step "Building the signed Android APK for $($target.Abi)"
            $apkOut = Join-Path $publish "android\$($target.Abi)"
            if (Test-Path $apkOut) { Remove-Item -Recurse -Force $apkOut }
            # Single-RID builds share obj\Release: start clean so no packaging step is skipped as "up to date"
            # with the previous CPU type's files.
            foreach ($dir in 'src\JackiVpn.Mobile\bin\Release', 'src\JackiVpn.Mobile\obj\Release') {
                if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
            }
            Invoke-Dotnet publish src\JackiVpn.Mobile\JackiVpn.Mobile.csproj -f net10.0-android -c Release --nologo -o $apkOut `
                "-p:JackiAbiRid=$($target.Rid)" "-p:JackiSigningKeyStore=$keystore" "-p:JackiSigningPasswordFile=$passwordFile"

            $apk = Join-Path $apkOut 'com.jacki.vpn-Signed.apk'
            & $java -jar $apksigner verify $apk
            if ($LASTEXITCODE -ne 0) { throw "The $($target.Abi) APK is not correctly signed." }
            Copy-Item $apk (Join-Path $release "IBOX-$version-android-$($target.Abi).apk") -Force
        }
    }

    Copy-Item THIRD_PARTY_NOTICES.txt $release -Force
    Copy-Item installer\INSTALL-FA.txt $release -Force
    Copy-Item installer\INSTALL-EN.txt $release -Force

    $sums = Get-ChildItem $release -File | Where-Object { $_.Extension -in '.msi', '.apk' } | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant(), $_.Name
    }
    [IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'), [string[]]$sums)

    Write-Step 'Done'
    Get-ChildItem $release -File | ForEach-Object { '{0,-48} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
}
finally {
    Pop-Location
}
