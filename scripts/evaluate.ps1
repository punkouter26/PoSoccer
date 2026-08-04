# Evaluation harness (v1 spec): runs the headless player in eval mode and judges
# the result against the acceptance bar (>=80% blue wins, <=10% stalemates).
#   Baseline (harness validation, ~50% expected):  .\scripts\evaluate.ps1 -Baseline -Episodes 40
#   Model eval:                                    .\scripts\evaluate.ps1 -RunId soccer_p1_00 -Episodes 100
param(
    [string]$RunId = "soccer_p1_00",
    # 2026-08-04: default raised 100 -> 1000. Measured across ten identical-model
    # runs, a 100-episode eval scatters 11%-24% (SD ~3.7 points, i.e. ordinary
    # binomial noise at n=100 - nothing is broken, the sample was just too small).
    # At that resolution a real +5-point gain is invisible and a lucky run reads
    # as progress: a single 24% was briefly recorded as an improvement over 18%
    # when the true mean was 16.2%. 1000 episodes drops SD to ~1.2 points and
    # costs a few minutes.
    [int]$Episodes = 1000,
    [switch]$Baseline,
    [ValidateSet("STANDARD", "MATT", "KIM", "NICK")]
    [string]$Profile = "STANDARD",
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

# Agent asset folders follow <AgentName>_v<NN> (UNITY_RULES 1).
$folders = @{ STANDARD = "Standard_v01"; MATT = "Matt_v01"; KIM = "Kim_v01"; NICK = "Nick_v01" }
$model = Join-Path $root "Assets\Agents\$($folders[$Profile])\$Profile.onnx"
if (-not $Baseline -and -not (Test-Path $model)) {
    Write-Error "No model at $model - train first, then scripts\update-model.ps1 -RunId <run> -Profile $Profile."
    exit 2
}

# The player evaluates whatever model is baked into the scene, so a stale build
# silently evaluates the wrong weights. Warn when the build predates the model.
if (-not $Baseline -and (Test-Path $exe)) {
    $modelTime = (Get-Item $model).LastWriteTime
    $buildTime = (Get-Item $exe).LastWriteTime
    if ($modelTime -gt $buildTime) {
        Write-Warning ("Model ($($modelTime.ToString('HH:mm:ss'))) is newer than the player build " +
                       "($($buildTime.ToString('HH:mm:ss'))). Re-assign m_Model and rebuild, or this " +
                       "evaluates stale weights.")
    }
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

# Stamp the measured rating onto the profile so the menu can show it next to the
# step count. Baseline runs grade the bot, not a brain, so they never write.
if (-not $Baseline) {
    . "$PSScriptRoot\lib-profile.ps1"
    $profileAsset = Join-Path $root "Assets\Agents\$($folders[$Profile])\Reward_$Profile.asset"
    Set-ProfileField $profileAsset 'evalWinRate' ("{0:0.####}" -f $winRate)
    Set-ProfileField $profileAsset 'evalEpisodes' "$($r.episodes)"
    Write-Host ("  stamped Reward_{0}.asset: {1:P1} over {2} episodes" -f $Profile, $winRate, $r.episodes)
}

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
