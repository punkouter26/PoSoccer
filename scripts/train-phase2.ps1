# Phase 2 MA-POCA self-play training (1v1 -> 3v3).
# Headless parallel training per UNITY_RULES: dedicated base port, 4-8 logical cores.
# Usage: .\scripts\train-phase2.ps1 -RunId soccer_p2_00 -EnvPath Builds\PoSoccer\PoSoccer.exe
#        Add -InitFrom soccer_p1_00 to warm-start from the Phase 1 policy.
param(
    [string]$RunId = "soccer_p2_00",
    [string]$EnvPath = "",
    [int]$NumEnvs = 4,
    [int]$BasePort = 5005,
    [string]$InitFrom = "",
    [switch]$Resume,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
# Prefer .venv2 if it exists (the canonical .venv can be locked by Unity at
# runtime; .venv2 is created out-of-band by uv with the pinned interpreter
# when .venv is unusable. Both are valid; the newer one wins).
$venv = if (Test-Path "$root\.venv2\Scripts\Activate.ps1") { "$root\.venv2" } else { "$root\.venv" }
& "$venv\Scripts\Activate.ps1"

# Optional observability nudge: warn if TensorBoard isn't responding.
# (UNITY_RULES 4: track active TensorBoard sessions.)
try {
    $null = Invoke-WebRequest -Uri "http://localhost:6006/" -UseBasicParsing -TimeoutSec 2
} catch {
    Write-Warning "TensorBoard is not reachable on :6006. Run .\scripts\tensorboard.ps1 in another terminal to watch reward convergence / policy entropy / value loss. Training will continue."
}

$mlArgs = @("$root\config\STANDARD_phase2_poca.yaml",
          "--run-id=$RunId", "--base-port=$BasePort", "--results-dir=$root\results")
if ($EnvPath)  { $mlArgs += @("--env=$root\$EnvPath", "--no-graphics", "--num-envs=$NumEnvs") }
if ($InitFrom) { $mlArgs += "--initialize-from=$InitFrom" }
if ($Resume)   { $mlArgs += "--resume" }
if ($Force)    { $mlArgs += "--force" }

try {
    mlagents-learn @mlArgs
}
finally {
    & "$root\scripts\cleanup-training.ps1"
    & "$root\scripts\update-model.ps1" -RunId $RunId
}
