# Evaluation harness (v1 spec): runs the headless player in eval mode and judges
# the result against the acceptance bar (>=80% blue wins, <=10% stalemates).
#   Baseline (harness validation, ~50% expected):  .\scripts\evaluate.ps1 -Baseline -Episodes 40
#   Model eval:                                    .\scripts\evaluate.ps1 -RunId soccer_p1_00 -Episodes 100
param(
    [string]$RunId = "soccer_p1_00",
    [int]$Episodes = 100,
    [switch]$Baseline,
    [string]$ExePath = "Builds\PoSoccer\PoSoccer.exe",
    [int]$TimeoutMin = 30,
    [switch]$Rebuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root $ExePath

if ($Rebuild -or -not (Test-Path $exe)) {
    Write-Host "Building headless player..."
    & "$root\scripts\build-headless.ps1"
}

$model = Join-Path $root "Assets\Agents\Standard_v01\STANDARD.onnx"
if (-not $Baseline -and -not (Test-Path $model)) {
    Write-Error "No model at $model - train first (scripts\train-phase1.ps1), then scripts\update-model.ps1 -RunId <run> and assign the .onnx to both agents' BehaviorParameters."
    exit 2
}

$tag = if ($Baseline) { "baseline" } else { $RunId }
$out = Join-Path $root "results\eval\$tag.json"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
if (Test-Path $out) { Remove-Item $out -Force -Confirm:$false }

$env:POSOCCER_EVAL = "1"
$env:POSOCCER_BASELINE = if ($Baseline) { "1" } else { "" }
$env:POSOCCER_EPISODES = "$Episodes"
$env:POSOCCER_RUNID = $tag
$env:POSOCCER_OUT = $out

try {
    $p = Start-Process -FilePath $exe -ArgumentList "-batchmode", "-nographics", `
        "-logFile", (Join-Path $root "Logs\eval-$tag.log") -PassThru
    $deadline = (Get-Date).AddMinutes($TimeoutMin)

    while (-not (Test-Path $out) -and -not $p.HasExited) {
        if ((Get-Date) -gt $deadline) {
            Write-Warning "Eval timed out after $TimeoutMin min - killing player."
            Stop-Process -Id $p.Id -Force -Confirm:$false
            & "$root\scripts\cleanup-training.ps1"
            exit 3
        }
        Start-Sleep -Seconds 5
    }
    # JSON is written just before Application.Quit; give the file a moment.
    Start-Sleep -Seconds 3
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -Confirm:$false -ErrorAction SilentlyContinue }
}
finally {
    $env:POSOCCER_EVAL = ""; $env:POSOCCER_BASELINE = ""
    $env:POSOCCER_EPISODES = ""; $env:POSOCCER_RUNID = ""; $env:POSOCCER_OUT = ""
}

if (-not (Test-Path $out)) {
    Write-Error "Eval produced no report at $out - see Logs\eval-$tag.log"
    exit 3
}

$r = Get-Content $out -Raw | ConvertFrom-Json
$winRate = if ($r.episodes) { $r.blueWins / $r.episodes } else { 0 }
$staleRate = if ($r.episodes) { $r.stalemates / $r.episodes } else { 1 }
Write-Host ("Eval '{0}': {1} episodes | blue {2} / red {3} / stale {4} | win-rate {5:P1} | mean steps {6:N0}" -f `
    $r.runId, $r.episodes, $r.blueWins, $r.redWins, $r.stalemates, $winRate, $r.meanEpisodeSteps)

if ($r.invalid) { Write-Error "Run marked INVALID (no model at eval time)."; exit 2 }

if ($Baseline) {
    Write-Host "Baseline run (no bar applied) - expect roughly 50/50 between mirrored heuristic bots."
    exit 0
}

if ($winRate -ge 0.80 -and $staleRate -le 0.10) {
    Write-Host "PASS: acceptance bar met (>=80% wins, <=10% stalemates)." -ForegroundColor Green
    exit 0
}
Write-Host "FAIL: below acceptance bar (>=80% wins, <=10% stalemates)." -ForegroundColor Red
exit 1
