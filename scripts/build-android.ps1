# build-android.ps1 - One-shot Unity APK builder for PoSoccer.
# Run with the editor CLOSED (per CLAUDE.md: "build-headless.ps1 only works
# with the editor closed"). The script refuses to run if Unity is in any
# process table and tells you to close it first.
#
# Output: Builds/PoSoccer/PoSoccer.apk (signed with the auto-generated
# debug keystore, ARM64 only, IL2CPP, Min SDK 26 / Target SDK 34).
#
# Usage:
#   .\scripts\build-android.ps1                       # release APK
#   .\scripts\build-android.ps1 -Development         # development build with profiler

[CmdletBinding()]
param(
    [switch]$Development
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = (Resolve-Path "$PSScriptRoot\..").Path
$LogsDir = Join-Path $ProjectRoot 'Logs'
$BuildDir = Join-Path $ProjectRoot 'Builds\PoSoccer'
$ApkPath = Join-Path $BuildDir 'PoSoccer.apk'
$LogPath = Join-Path $LogsDir 'AndroidBuild.log'
$UnityEditor = 'C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Unity.exe'

New-Item -ItemType Directory -Force -Path $LogsDir, $BuildDir | Out-Null

# 1. Editor must be closed before this script runs.
#    Per CLAUDE.md: "scripts/build-headless.ps1 only works with the editor
#    closed" - a daemon instance holds a write lock on ProjectLibrary.
$unityProcs = Get-Process -Name 'Unity' -ErrorAction SilentlyContinue
if ($unityProcs) {
    $names = $unityProcs | ForEach-Object { "$($_.ProcessName) PID=$($_.Id)" } | Join-String -Separator ', '
    Write-Host "ERROR: Unity is still running ($names). Close the Unity editor first." -ForegroundColor Red
    Write-Host "       File -> Exit. (The mcp-for-unity bridge will shut down with it.)" -ForegroundColor Yellow
    exit 2
}

# 2. Sanity-check the Android player module is installed.
$apkMarker = 'C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Data\PlaybackEngines\AndroidPlayer\Apk'
if (-not (Test-Path $apkMarker)) {
    Write-Host "ERROR: AndroidPlayer module not installed at $apkMarker" -ForegroundColor Red
    Write-Host "       Install it via Unity Hub -> 6000.5.6f1 -> Add Modules -> Android Build Support." -ForegroundColor Yellow
    exit 3
}

# 3. Sanity-check the scenes exist and the manifest is on disk.
$scenes = @(
    'Assets/Scenes/SCN_Training.unity',
    'Assets/Scenes/SCN_Menu.unity',
    'Assets/Scenes/SCN_Exhibition.unity'
)
foreach ($s in $scenes) {
    if (-not (Test-Path (Join-Path $ProjectRoot $s))) {
        Write-Host "ERROR: scene missing: $s" -ForegroundColor Red
        exit 4
    }
}
# Product name and Android bundle identifier are set in ProjectSettings.asset
# (productName=PoSoccer, applicationIdentifier.Android=com.posoccer.app).
# Unity 6 removed support for Assets/Plugins/Android/res overrides, so we
# rely on EditorUserBuildSettings to bake the right values into the gradle
# build.

# 4. Build the Unity command line.
#    -batchmode            Headless
#    -quit                 Force-exit when build is done
#    -nographics           No GPU required (we never spawn one)
#    -buildTarget Android  Goes through Gradle, single APK
#    -executeMethod ...    Standard Unity build entry point
#    -logFile -            stdout to log file (one stream)
$buildType = if ($Development) { 'Development' } else { 'Master' }
Write-Host "[1/4] Building $buildType APK -> $ApkPath" -ForegroundColor Cyan
Write-Host "       log: $LogPath" -ForegroundColor DarkCyan

$proc = Start-Process -FilePath $UnityEditor `
    -ArgumentList @(
        '-batchmode',
        '-quit',
        '-nographics',
        '-projectPath', $ProjectRoot,
        '-buildTarget', 'Android',
        '-executeMethod', 'Agent_BuildPlayerCommand.Build',
        '-buildTargetGroup', 'Android',
        '-buildTargetSubtarget', '0',
        '-logFile', $LogPath
    ) `
    -NoNewWindow -PassThru -Wait

if ($proc.ExitCode -ne 0) {
    Write-Host "ERROR: Unity exited with code $($proc.ExitCode)" -ForegroundColor Red
    Write-Host "       Last 60 lines of ${LogPath}:" -ForegroundColor Yellow
    Get-Content $LogPath -Tail 60 | ForEach-Object { "  $_" }
    exit $proc.ExitCode
}

# 5. Wait for the manifest to materialize (Unity writes the APK at the end).
Write-Host "[2/4] Waiting for $ApkPath" -ForegroundColor Cyan
$timeout = (Get-Date).AddMinutes(20)
while (-not (Test-Path $ApkPath) -and (Get-Date) -lt $timeout) {
    Start-Sleep -Seconds 2
}

if (-not (Test-Path $ApkPath)) {
    Write-Host "ERROR: ${ApkPath} did not appear within 20 minutes." -ForegroundColor Red
    Write-Host "       Last 60 lines of ${LogPath}:" -ForegroundColor Yellow
    Get-Content $LogPath -Tail 60 | ForEach-Object { "  $_" }
    exit 6
}

# 6. Confirm and report.
$apkSize = (Get-Item $ApkPath).Length
$apkSizeMb = [Math]::Round($apkSize / 1MB, 1)
Write-Host "[3/4] Build succeeded: $ApkPath ($apkSizeMb MB)" -ForegroundColor Green

# 7. Optional sanity-check: APK signature.
Write-Host "[4/4] Verifying APK signature" -ForegroundColor Cyan
$apksigner = Get-Command apksigner -ErrorAction SilentlyContinue
if ($apksigner) {
    & apksigner verify --verbose $ApkPath 2>&1 | Select-Object -First 6 | ForEach-Object { "  $_" }
} else {
    Write-Host "  (apksigner not on PATH; skipping verify. Run 'apksigner verify --verbose $ApkPath' manually.)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "SIDE-LOAD INSTRUCTIONS:" -ForegroundColor Cyan
Write-Host "  1. On your phone: Settings -> Developer options -> enable USB debugging" -ForegroundColor White
Write-Host "  2. Plug via USB cable; tap 'Allow' on the RSA fingerprint popup" -ForegroundColor White
Write-Host "  3. From this machine: adb install -r $ApkPath" -ForegroundColor White
Write-Host "  4. Launch PoSoccer from the app drawer (icon: 'PoSoccer')" -ForegroundColor White
Write-Host ""
Write-Host "  Alternative: copy the APK to your phone and tap it in Files app." -ForegroundColor DarkCyan
