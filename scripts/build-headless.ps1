# Produce the headless training build used by --env (no editor scripts needed:
# Unity's CLI -buildWindows64Player builds the scenes in Build Settings).
param(
    [string]$UnityExe = "C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe",
    [string]$Output = "Builds\PoSoccer\PoSoccer.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

& $UnityExe -batchmode -quit -projectPath $root `
    -buildWindows64Player (Join-Path $root $Output) `
    -logFile (Join-Path $root "Logs\headless-build.log")

if ($LASTEXITCODE -ne 0) { throw "Build failed - see Logs\headless-build.log" }
Write-Host "OK: headless training build at $Output"
