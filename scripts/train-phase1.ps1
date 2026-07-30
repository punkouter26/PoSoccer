# Phase 1 PPO bootstrap training vs the heuristic bot.
# In-editor:  .\scripts\train-phase1.ps1 -RunId soccer_p1_00      (press Play when prompted)
# Headless:   .\scripts\train-phase1.ps1 -RunId soccer_p1_00 -EnvPath Builds\PoSoccer\PoSoccer.exe -NumEnvs 4
param(
    [string]$RunId = "soccer_p1_00",
    [string]$EnvPath = "",
    [int]$NumEnvs = 4,
    [int]$BasePort = 5005,
    [switch]$Resume,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
& "$root\.venv\Scripts\Activate.ps1"

$args = @("$root\config\SoccerAgent_v01_phase1_ppo.yaml",
          "--run-id=$RunId", "--base-port=$BasePort", "--results-dir=$root\results")
if ($EnvPath) { $args += @("--env=$root\$EnvPath", "--no-graphics", "--num-envs=$NumEnvs") }
if ($Resume)  { $args += "--resume" }
if ($Force)   { $args += "--force" }

try {
    mlagents-learn @args
}
finally {
    # Lifecycle guardrail: no orphaned trainer/env processes after a run.
    & "$root\scripts\cleanup-training.ps1"
    # Auto-assign the freshest checkpoint into the agent prefab slot (GUID preserved).
    & "$root\scripts\update-model.ps1" -RunId $RunId
}
