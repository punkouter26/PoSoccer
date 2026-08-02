# Trains every personality brain in sequence on the rescaled pitch.
#
# POSOCCER_PROFILE is read in Agent_EnvController.Awake and swaps both agents
# onto that profile (and its behavior name) before the brain contract is frozen,
# so one build trains any brain without a scene edit per run.
#
#   .\scripts\train-all.ps1                       # all four, sequentially
#   .\scripts\train-all.ps1 -Profiles STANDARD    # just one
param(
    [string[]]$Profiles = @("STANDARD", "MATT", "KIM", "NICK"),
    [string]$EnvPath = "Builds\PoSoccer\PoSoccer.exe",
    [int]$NumEnvs = 4,
    [string]$Tag = "v2",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$py = Join-Path $root ".venv\Scripts\mlagents-learn.exe"
if (-not (Test-Path $py)) { throw "No trainer at $py - run scripts/setup-training-env.ps1 first." }

$env = Join-Path $root $EnvPath
if (-not (Test-Path $env)) { throw "No player build at $env - build SCN_Training first." }

$basePort = 5010
foreach ($name in $Profiles) {
    $runId = "soccer_${Tag}_$($name.ToLower())"
    $config = Join-Path $root "config\TRAIN_${name}_v2.yaml"
    if (-not (Test-Path $config)) { throw "Missing config $config - run scripts/write-configs-v2.ps1." }

    Write-Host ""
    Write-Host "=== training $name  (run-id $runId, port $basePort) ==="

    # Inherited by the player processes mlagents-learn spawns.
    $env:POSOCCER_PROFILE = $name

    $mlArgs = @($config, "--run-id=$runId", "--base-port=$basePort",
                "--results-dir=$root\results", "--env=$env",
                "--no-graphics", "--num-envs=$NumEnvs")
    if ($Force) { $mlArgs += "--force" }

    & $py @mlArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "$name exited with code $LASTEXITCODE - continuing to the next profile."
    }

    # Promote straight into the personality's GUID-stable slot.
    & (Join-Path $PSScriptRoot "update-model.ps1") -RunId $runId -Profile $name

    $basePort += 20   # keep concurrent/leftover workers from colliding
}

Remove-Item Env:\POSOCCER_PROFILE -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "All requested profiles finished. Inspect with: .\scripts\tensorboard.ps1"
