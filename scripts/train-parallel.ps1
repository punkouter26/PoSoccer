# Train several personality profiles AT THE SAME TIME, one mlagents-learn per profile.
#
#   .\scripts\tensorboard.ps1
#   .\scripts\train-parallel.ps1 -Tag p31                       # all four, config TRAIN_<NAME>_p31.yaml
#   .\scripts\train-parallel.ps1 -Tag p31 -Profiles MATT,KIM
#
# WHY THIS EXISTS BESIDE train-all.ps1. train-all runs the profiles one after another,
# and one run uses about 2 of this machine's 12 logical cores (the trainer is a single
# thread; see CLAUDE.md), so four sequential runs leave the machine mostly idle for
# roughly seven hours. train-phase1.ps1 cannot simply be started four times either: its
# `finally` block calls cleanup-training.ps1, which kills EVERY trainer and env player on
# the machine, so the first run to finish would take the other three down with it.
#
# Each profile gets its own worker shell, its own port range and its own log in
# results\train_<runId>.log. The worker deploys its model with update-model.ps1 when its
# trainer exits; nothing here kills another run. Run cleanup-training.ps1 yourself
# afterwards if a worker died and left env players behind.
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [ValidateSet("STANDARD", "MATT", "KIM", "NICK")]
    [string[]]$Profiles = @("STANDARD", "MATT", "KIM", "NICK"),
    [string]$ConfigTag = "",
    [string]$EnvPath = "Builds\PoSoccer\PoSoccer.exe",
    [int]$NumEnvs = 4,
    [string]$Squad = "",
    [switch]$Force,
    # Internal: set when this script re-invokes itself as one profile's worker.
    [string]$WorkerProfile = "",
    [int]$WorkerPort = 0
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not $ConfigTag) { $ConfigTag = $Tag }

if ($WorkerProfile) {
    # ---- one profile's worker -------------------------------------------------
    $name = $WorkerProfile
    $runId = "soccer_${Tag}_$($name.ToLower())"
    $venv = if (Test-Path "$root\.venv2\Scripts\Activate.ps1") { "$root\.venv2" } else { "$root\.venv" }
    & "$venv\Scripts\Activate.ps1"

    # Same environment train-phase1.ps1 and train-all.ps1 set: face the scripted bot,
    # and swap both agents onto this profile's reward table and behaviour name.
    $env:POSOCCER_OPPONENT = "bot"
    $env:POSOCCER_SQUAD = $Squad
    $env:POSOCCER_PROFILE = $name

    $mlArgs = @((Join-Path $root "config\TRAIN_${name}_${ConfigTag}.yaml"),
                "--run-id=$runId", "--base-port=$WorkerPort", "--results-dir=$root\results",
                "--env=$root\$EnvPath", "--no-graphics", "--num-envs=$NumEnvs")
    if ($Force) { $mlArgs += "--force" }

    mlagents-learn @mlArgs
    $code = $LASTEXITCODE
    Write-Host "[$name] mlagents-learn exited with code $code"
    if ($code -eq 0) {
        & "$root\scripts\update-model.ps1" -RunId $runId -Profile $name
    }
    exit $code
}

# ---- launcher ---------------------------------------------------------------
if (-not (Test-Path (Join-Path $root $EnvPath))) { throw "No player at $EnvPath - build it first." }
foreach ($name in $Profiles) {
    $config = Join-Path $root "config\TRAIN_${name}_${ConfigTag}.yaml"
    if (-not (Test-Path $config)) { throw "Config not found: $config" }
}
try {
    $null = Invoke-WebRequest -Uri "http://localhost:6006/" -UseBasicParsing -TimeoutSec 2
} catch {
    Write-Warning "TensorBoard is not reachable on :6006. Run .\scripts\tensorboard.ps1 first."
}
New-Item -ItemType Directory -Force (Join-Path $root "results") | Out-Null

# Port ranges are 10 apart and skip 5037, which adb listens on (see train-all.ps1).
$ports = @{ STANDARD = 5005; MATT = 5015; KIM = 5025; NICK = 5045 }
if ($NumEnvs -gt 8) { throw "-NumEnvs above 8 would overlap the per-profile port ranges." }

$self = $MyInvocation.MyCommand.Path
$workers = @()
foreach ($name in $Profiles) {
    $runId = "soccer_${Tag}_$($name.ToLower())"
    $log = Join-Path $root "results\train_$runId.log"
    $workerArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $self,
                    '-Tag', $Tag, '-ConfigTag', $ConfigTag, '-EnvPath', $EnvPath,
                    '-NumEnvs', "$NumEnvs", '-WorkerProfile', $name, '-WorkerPort', "$($ports[$name])")
    if ($Squad) { $workerArgs += @('-Squad', $Squad) }
    if ($Force) { $workerArgs += '-Force' }
    $p = Start-Process pwsh -ArgumentList $workerArgs -WorkingDirectory $root -WindowStyle Hidden -PassThru `
                       -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    Write-Host "$name -> run $runId, base port $($ports[$name]), worker PID $($p.Id), log $log"
    $workers += $p
}
Write-Host "PIDS: $(($workers | ForEach-Object { $_.Id }) -join ',')"
