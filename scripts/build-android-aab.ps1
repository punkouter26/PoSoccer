<#
.SYNOPSIS
  Build the Play-uploadable Android App Bundle (.aab) for PoSoccer.

.DESCRIPTION
  The sibling of build-android.ps1, which produces a debug-signed side-load APK.
  That artifact cannot go to Play: Play needs an App Bundle, target API 36, and
  a real upload key. This script produces all three.

  Signing credentials are read from environment variables and are never written
  to disk by this script. Set them in your shell before running:

    $env:POSOCCER_KEYSTORE      = "C:\keys\posoccer-upload.keystore"
    $env:POSOCCER_KEYSTORE_PASS = "..."
    $env:POSOCCER_KEYALIAS      = "posoccer"
    $env:POSOCCER_KEYALIAS_PASS = "..."

  If you have no keystore yet, create one FIRST (choose your own password; keep
  it somewhere safe - losing it means you can never update the app again):

    & "$env:JAVA_HOME\bin\keytool.exe" -genkeypair -v `
        -keystore C:\keys\posoccer-upload.keystore `
        -alias posoccer -keyalg RSA -keysize 2048 -validity 10000

  Unity ships a JDK at:
    C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK\bin\keytool.exe

.EXAMPLE
  .\scripts\build-android-aab.ps1
  .\scripts\build-android-aab.ps1 -VersionName 0.1.1 -VersionCode 4
#>
[CmdletBinding()]
param(
    [string]$VersionName,
    [int]$VersionCode = 0,
    [string]$UnityExe
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = (Resolve-Path "$PSScriptRoot\..").Path
$LogsDir = Join-Path $ProjectRoot 'Logs'
$BuildDir = Join-Path $ProjectRoot 'Builds\PoSoccer'
$AabPath = Join-Path $BuildDir 'PoSoccer.aab'
$LogPath = Join-Path $LogsDir 'AndroidAabBuild.log'

New-Item -ItemType Directory -Force -Path $LogsDir, $BuildDir | Out-Null

# Resolve the editor from ProjectVersion.txt rather than hardcoding it the way
# build-headless.ps1 does (it still points at 6000.5.4f1, which is not this
# project's version).
if (-not $UnityExe) {
    $verLine = Select-String -Path (Join-Path $ProjectRoot 'ProjectSettings\ProjectVersion.txt') `
                             -Pattern '^m_EditorVersion:' | Select-Object -First 1
    $ver = ($verLine.Line -split ':', 2)[1].Trim()
    $UnityExe = "C:\Program Files\Unity\Hub\Editor\$ver\Editor\Unity.exe"
}
if (-not (Test-Path $UnityExe)) { throw "No Unity editor at $UnityExe" }

# 1. Editor must be closed - a running editor holds the ProjectLibrary lock.
$unityProcs = Get-Process -Name 'Unity' -ErrorAction SilentlyContinue
if ($unityProcs) {
    $names = ($unityProcs | ForEach-Object { "$($_.ProcessName) PID=$($_.Id)" }) -join ', '
    Write-Host "ERROR: Unity is still running ($names). Close the editor first (File -> Exit)." -ForegroundColor Red
    exit 2
}

# 2. Signing must be configured, or the build wastes 20 minutes to produce
#    something Play will reject.
foreach ($v in 'POSOCCER_KEYSTORE', 'POSOCCER_KEYSTORE_PASS', 'POSOCCER_KEYALIAS', 'POSOCCER_KEYALIAS_PASS') {
    if (-not (Get-Item "Env:\$v" -ErrorAction SilentlyContinue)) {
        Write-Host "ERROR: `$env:$v is not set. See the header of this script." -ForegroundColor Red
        exit 3
    }
}
if (-not (Test-Path $env:POSOCCER_KEYSTORE)) {
    Write-Host "ERROR: no keystore at $env:POSOCCER_KEYSTORE" -ForegroundColor Red
    exit 3
}

# 3. Android SDK Platform 36 must be present. Unity's batchmode SDK manager
#    does not reliably fetch a missing platform, and the failure surfaces deep
#    in a Gradle log rather than as a clear error.
$sdkRoot = Join-Path (Split-Path $UnityExe -Parent) 'Data\PlaybackEngines\AndroidPlayer\SDK'
$platform36 = Join-Path $sdkRoot 'platforms\android-36'
if (-not (Test-Path $platform36)) {
    Write-Host "WARNING: Android SDK Platform 36 not found at $platform36" -ForegroundColor Yellow
    Write-Host "         Play requires target API 36 for new apps from 2026-08-31." -ForegroundColor Yellow
    Write-Host "         Install it with the SDK manager, e.g.:" -ForegroundColor Yellow
    Write-Host "           & '$sdkRoot\cmdline-tools\<ver>\bin\sdkmanager.bat' 'platforms;android-36'" -ForegroundColor Yellow
    Write-Host "         Continuing - the build will fail in Gradle if it is genuinely absent." -ForegroundColor Yellow
}

if ($VersionName) { $env:POSOCCER_VERSION_NAME = $VersionName }
if ($VersionCode -gt 0) { $env:POSOCCER_VERSION_CODE = "$VersionCode" }

if (Test-Path $AabPath) { Remove-Item $AabPath -Force }

Write-Host "[1/3] Building AAB -> $AabPath" -ForegroundColor Cyan
Write-Host "       log: $LogPath" -ForegroundColor DarkCyan

$proc = Start-Process -FilePath $UnityExe `
    -ArgumentList @(
        '-batchmode', '-quit', '-nographics',
        '-projectPath', $ProjectRoot,
        '-buildTarget', 'Android',
        '-executeMethod', 'BuildAabCommand.Build',
        '-logFile', $LogPath
    ) -NoNewWindow -PassThru -Wait

if ($proc.ExitCode -ne 0) {
    Write-Host "ERROR: Unity exited with code $($proc.ExitCode)" -ForegroundColor Red
    switch ($proc.ExitCode) {
        2 { Write-Host "       A scene listed in BuildAabCommand.Scenes is missing." -ForegroundColor Yellow }
        3 { Write-Host "       Signing configuration was rejected inside Unity." -ForegroundColor Yellow }
    }
    Write-Host "       Last 60 lines of ${LogPath}:" -ForegroundColor Yellow
    if (Test-Path $LogPath) { Get-Content $LogPath -Tail 60 | ForEach-Object { "  $_" } }
    exit $proc.ExitCode
}

if (-not (Test-Path $AabPath)) {
    Write-Host "ERROR: build reported success but $AabPath does not exist." -ForegroundColor Red
    exit 6
}

$sizeMb = [Math]::Round((Get-Item $AabPath).Length / 1MB, 1)
Write-Host "[2/3] Built $AabPath ($sizeMb MB)" -ForegroundColor Green

# 4. Report what Play will actually see, from the log the build just wrote.
Write-Host "[3/3] Manifest values baked into this bundle:" -ForegroundColor Cyan
Select-String -Path $LogPath -Pattern '\[BuildAabCommand\] (appId|Result|Size)=?' |
    ForEach-Object { "  $($_.Line.Trim())" }

Write-Host ""
Write-Host "Upload this file to Play Console -> Testing -> Internal testing -> Create new release." -ForegroundColor Cyan
Write-Host "Keep the keystore and its password backed up. Losing them means you can never" -ForegroundColor Yellow
Write-Host "publish an update to this app under the same package name." -ForegroundColor Yellow
