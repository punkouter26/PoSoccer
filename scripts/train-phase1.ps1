# Phase 1 PPO bootstrap training vs the heuristic bot.
# In-editor:  .\scripts\train-phase1.ps1 -RunId soccer_p1_00      (press Play when prompted)
# Headless:   .\scripts\train-phase1.ps1 -RunId soccer_p1_00 -EnvPath Builds\PoSoccer\PoSoccer.exe -NumEnvs 4
param(
    [string]$RunId = "soccer_p1_00",
    [string]$EnvPath = "",
    [int]$NumEnvs = 4,
    [int]$BasePort = 5005,
    [string]$Config = "STANDARD_phase1_ppo.yaml",
    [string]$InitFrom = "",
    [switch]$Resume,
    [switch]$Force,
    [Parameter(HelpMessage = "Leave both teams on the trainer (symmetric self-play) instead of facing the bot.")]
    [switch]$SelfPlay
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

# Phase 1 means "learn against the scripted bot". The training scene leaves both
# teams on BehaviorType.Default, which routes everything to the trainer, so
# without this the run is symmetric self-play and Agent_HeuristicBot never
# executes - which is what silently happened to every run before 2026-08-04.
# Agent_Soccer.ApplyTrainingOpponent reads this and forces Red to HeuristicOnly.
# Env players are spawned as children of mlagents-learn, so they inherit it.
$env:POSOCCER_OPPONENT = if ($SelfPlay) { "" } else { "bot" }

$mlArgs = @("$root\config\$Config",
          "--run-id=$RunId", "--base-port=$BasePort", "--results-dir=$root\results")
if ($EnvPath) { $mlArgs += @("--env=$root\$EnvPath", "--no-graphics", "--num-envs=$NumEnvs") }
if ($InitFrom) { $mlArgs += "--initialize-from=$InitFrom" }
if ($Resume)  { $mlArgs += "--resume" }
if ($Force)   { $mlArgs += "--force" }

try {
    mlagents-learn @mlArgs
}
finally {
    $env:POSOCCER_OPPONENT = ""
    # Lifecycle guardrail: no orphaned trainer/env processes after a run.
    & "$root\scripts\cleanup-training.ps1"
    # Auto-assign the freshest checkpoint into the agent prefab slot (GUID preserved).
    & "$root\scripts\update-model.ps1" -RunId $RunId
}
