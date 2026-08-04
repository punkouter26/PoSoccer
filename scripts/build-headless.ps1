# Produce the headless training build used by --env (no editor scripts needed:
# Unity's CLI -buildWindows64Player builds the scenes in Build Settings).
param(
    [string]$UnityExe = "",
    [string]$Output = "Builds\PoSoccer\PoSoccer.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# Resolve the editor from ProjectSettings/ProjectVersion.txt rather than a
# hardcoded path. This was pinned to 6000.5.4f1 and silently broke the moment
# the project moved to 6000.5.6f1 - every call failed with "not recognized as
# a name of a cmdlet", which reads like a PowerShell problem rather than a
# stale version pin. Deriving it from the project also guarantees the build
# uses the same editor the project opens with.
if (-not $UnityExe) {
    $versionFile = Join-Path $root "ProjectSettings\ProjectVersion.txt"
    if (-not (Test-Path $versionFile)) { throw "Missing $versionFile - cannot resolve the Unity version." }
    $version = (Select-String -Path $versionFile -Pattern '^m_EditorVersion:\s*(\S+)').Matches[0].Groups[1].Value
    $UnityExe = "C:\Program Files\Unity\Hub\Editor\$version\Editor\Unity.exe"
    if (-not (Test-Path $UnityExe)) {
        throw "Unity $version (from ProjectVersion.txt) is not installed at $UnityExe. " +
              "Install it via Unity Hub, or pass -UnityExe explicitly."
    }
    Write-Host "Using Unity $version"
}

$log = Join-Path $root "Logs\headless-build.log"
$outPath = Join-Path $root $Output
$before = if (Test-Path $outPath) { (Get-Item $outPath).LastWriteTimeUtc } else { [datetime]::MinValue }

# Start-Process -Wait rather than the call operator: `&` has been observed
# returning before Unity's child process finished, so $LASTEXITCODE reported a
# failure on a build whose log ended with "Exiting batchmode successfully now!".
$proc = Start-Process -FilePath $UnityExe -Wait -PassThru -NoNewWindow -ArgumentList @(
    "-batchmode", "-quit", "-projectPath", $root,
    "-buildWindows64Player", $outPath,
    "-logFile", $log
)

# Trust the artifact over the exit code: a platform switch (e.g. after an
# Android build) makes Unity reimport everything and can muddy the return value.
$after = if (Test-Path $outPath) { (Get-Item $outPath).LastWriteTimeUtc } else { [datetime]::MinValue }
$dataDir = Join-Path (Split-Path $outPath -Parent) ((Split-Path $outPath -LeafBase) + "_Data")
$dataFresh = (Test-Path $dataDir) -and
             ((Get-ChildItem $dataDir -Filter "*.assets" -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc -gt $before)

if (($after -le $before) -and -not $dataFresh) {
    throw "Build produced no new artifact (exit $($proc.ExitCode)) - see $log"
}
if ($proc.ExitCode -ne 0) {
    Write-Warning "Unity returned $($proc.ExitCode) but the player artifact is fresh; continuing."
}
Write-Host "OK: headless training build at $Output"
