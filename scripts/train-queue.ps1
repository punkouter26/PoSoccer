# Unattended overnight training queue.
#
# Runs a list of training jobs back to back, one at a time, and keeps going if
# one of them dies. Designed to be launched detached and left alone for hours.
#
#   .\scripts\train-queue.ps1 -BudgetHours 8
#
# WHY A QUEUE AND NOT ONE LONG RUN. CLAUDE.md records "train it longer" as a
# disproven lever: p4 (7M) and p5 (30M) both landed at ~16-17%. Spending the
# whole budget on a single longer run therefore re-tests the one hypothesis
# already known to fail. The queue instead spends it on four DIFFERENT questions,
# each a single variable, so the morning produces four answers rather than one
# number. The first job is the honest retest of compute itself, because that
# disproof was measured on an environment with two defects since fixed.
#
# Each job writes its own log under results/. Nothing here evaluates anything -
# evaluation needs a player rebuilt with the model baked in, which needs the
# editor, so it is a separate deliberate step in the morning.
param(
    [double]$BudgetHours = 8,
    [string]$EnvPath = "Builds\PoSoccer\PoSoccer.exe",
    [int]$NumEnvs = 4
)

$ErrorActionPreference = "Continue"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$deadline = (Get-Date).AddHours($BudgetHours)
$queueLog = Join-Path $root "results\queue.log"

function Write-Queue([string]$msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg
    Write-Host $line
    Add-Content -Path $queueLog -Value $line
}

# Ordered most-informative-first, so a truncated night still answers the
# important question. Each entry: RunId, Config, InitFrom (optional).
# NO -InitFrom anywhere. It makes mlagents call torch.load, which emits a
# FutureWarning on stderr, and train-phase1.ps1 runs with
# $ErrorActionPreference = "Stop" - so a harmless warning becomes a terminating
# error and the job dies in 12 seconds. Training from scratch also removes a
# confound: a warm start from p11 would make "did more compute help" partly a
# question about p11's checkpoint.
$jobs = @(
    @{ Id = 'soccer_p12_scale';     Config = 'STANDARD_phase12_scale.yaml';     InitFrom = '' },
    @{ Id = 'soccer_p13_curiosity'; Config = 'STANDARD_phase13_curiosity.yaml'; InitFrom = '' },
    @{ Id = 'soccer_p14_capacity';  Config = 'STANDARD_phase14_capacity.yaml';  InitFrom = '' },
    @{ Id = 'soccer_p15_poca';      Config = 'STANDARD_phase2_poca.yaml';       InitFrom = '' }
)

Write-Queue "QUEUE START - budget $BudgetHours h, deadline $($deadline.ToString('HH:mm:ss'))"
Write-Queue "env: $EnvPath  numEnvs: $NumEnvs"

foreach ($job in $jobs) {
    if ((Get-Date) -ge $deadline) {
        Write-Queue "BUDGET EXHAUSTED - skipping $($job.Id) and everything after it"
        break
    }

    $remaining = [math]::Round(($deadline - (Get-Date)).TotalMinutes)
    Write-Queue "START $($job.Id)  config=$($job.Config)  initFrom='$($job.InitFrom)'  (${remaining} min left)"

    # A previous attempt's directory makes mlagents-learn refuse to start.
    $resultDir = Join-Path $root "results\$($job.Id)"
    if (Test-Path $resultDir) {
        Write-Queue "  removing stale results dir for $($job.Id)"
        Remove-Item $resultDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    $jobLog = Join-Path $root "results\train_$($job.Id).log"
    # HASHTABLE splat, not an array. Two traps here, both hit already:
    #  - naming it $args collides with PowerShell's automatic variable, so the
    #    splat forwards THIS script's parameters (-BudgetHours) instead;
    #  - an ARRAY splat passes its elements positionally, so train-phase1.ps1
    #    bound '-EnvPath' as the value of its second positional parameter and
    #    died with "Cannot convert value '-EnvPath' to type System.Int32".
    # A hashtable binds by name and is immune to both.
    $jobArgs = @{
        RunId   = $job.Id
        EnvPath = $EnvPath
        NumEnvs = $NumEnvs
        Config  = $job.Config
    }
    if ($job.InitFrom) { $jobArgs['InitFrom'] = $job.InitFrom }

    $started = Get-Date
    try {
        & "$root\scripts\train-phase1.ps1" @jobArgs *>&1 | Tee-Object -FilePath $jobLog
    } catch {
        Write-Queue "  ERROR in $($job.Id): $($_.Exception.Message)"
    }
    $mins = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)

    $onnx = Join-Path $resultDir 'STANDARD.onnx'
    if (Test-Path $onnx) {
        Write-Queue "  DONE $($job.Id) in $mins min - exported"
    } else {
        Write-Queue "  FAILED $($job.Id) after $mins min - NO MODEL EXPORTED (see $jobLog)"
    }

    # Env players occasionally outlive the trainer; a leftover holding the port
    # makes the next job fail for an unrelated-looking reason.
    & "$root\scripts\cleanup-training.ps1" *>&1 | Out-Null
    Start-Sleep -Seconds 10
}

Write-Queue "QUEUE COMPLETE"
Write-Queue "Exported models:"
Get-ChildItem (Join-Path $root 'results') -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path (Join-Path $_.FullName 'STANDARD.onnx') } |
    ForEach-Object { Write-Queue "  $($_.Name)" }
