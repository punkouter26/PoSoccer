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
& "$root\.venv\Scripts\Activate.ps1"

$args = @("$root\config\SoccerAgent_v01_phase2_poca.yaml",
          "--run-id=$RunId", "--base-port=$BasePort", "--results-dir=$root\results")
if ($EnvPath)  { $args += @("--env=$root\$EnvPath", "--no-graphics", "--num-envs=$NumEnvs") }
if ($InitFrom) { $args += "--initialize-from=$InitFrom" }
if ($Resume)   { $args += "--resume" }
if ($Force)    { $args += "--force" }

try {
    mlagents-learn @args
}
finally {
    & "$root\scripts\cleanup-training.ps1"
    & "$root\scripts\update-model.ps1" -RunId $RunId
}
