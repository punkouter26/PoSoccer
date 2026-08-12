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
$dataDir = Join-Path (Split-Path $outPath -Parent) ((Split-Path $outPath -LeafBase) + "_Data")

# Baseline BOTH artifacts before the build. The freshness check below used to
# baseline only the .exe and then test the _Data/*.assets against that same
# timestamp - but Unity routinely rewrites the data files while leaving the .exe
# untouched, so once the data was newer than the .exe (which is the normal
# steady state) `$dataFresh` was permanently true and EVERY build reported
# success, including builds that never ran at all. That false green is how the
# 2026-08-05 player survived every phase-10 run: three training runs and three
# evals silently used a stale binary. Each artifact is now compared against its
# own prior timestamp.
function Get-NewestDataStamp($dir) {
    if (-not (Test-Path $dir)) { return [datetime]::MinValue }
    $newest = Get-ChildItem $dir -Filter "*.assets" -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($newest) { return $newest.LastWriteTimeUtc }
    return [datetime]::MinValue
}
$beforeExe  = if (Test-Path $outPath) { (Get-Item $outPath).LastWriteTimeUtc } else { [datetime]::MinValue }
$beforeData = Get-NewestDataStamp $dataDir

# A held project lock is the most common way this build silently does nothing.
# Catch it BEFORE launching Unity: the editor announces "another Unity instance
# is running with this project open" on the console and exits 1 without writing
# that line into -logFile, so there is nothing to detect afterwards - which is
# why the failure used to surface as a generic "no new artifact" and read like a
# code problem rather than "your editor is open".
$holder = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction SilentlyContinue |
          Where-Object { $_.CommandLine -match [regex]::Escape($root) -or
                         $_.CommandLine -match [regex]::Escape($root.Replace('\','/')) }
if ($holder) {
    throw ("The Unity editor has this project open (PID $(($holder.ProcessId) -join ', ')). " +
           "Batchmode cannot open the same project - close the editor and re-run, " +
           "or build from the running editor via MCP manage_build instead.")
}

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
# Each artifact is compared against its OWN pre-build timestamp (see above).
$afterExe  = if (Test-Path $outPath) { (Get-Item $outPath).LastWriteTimeUtc } else { [datetime]::MinValue }
$afterData = Get-NewestDataStamp $dataDir

if (($afterExe -le $beforeExe) -and ($afterData -le $beforeData)) {
    throw ("Build produced no new artifact (exit $($proc.ExitCode)) - see $log. " +
           "exe $beforeExe -> $afterExe, data $beforeData -> $afterData")
}
if ($proc.ExitCode -ne 0) {
    Write-Warning "Unity returned $($proc.ExitCode) but the player artifact is fresh; continuing."
}
Write-Host "OK: headless training build at $Output (data assets $afterData UTC)"
