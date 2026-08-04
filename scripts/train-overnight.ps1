<#
.SYNOPSIS
  Unattended overnight ML-Agents training queue with a hard wall-clock deadline.

.DESCRIPTION
  Runs a queue of mlagents-learn experiments back to back, promotes each result
  into the GUID-stable .onnx slot, rebuilds the headless player, evaluates
  against the rule-based bot, and writes one machine-readable summary the
  morning review reads.

  Designed to be launched by Windows Task Scheduler at midnight and to be
  finished and cleaned up by 08:00. It never runs past -EndTime: the remaining
  window is divided across the remaining runs, and a run that overshoots its
  slice is killed and exported from its newest checkpoint instead.

  Default queue (each ~20M steps, ~2.3 h at the measured 2.4k steps/s):
    A  STANDARD_phase3_opponent.yaml   opponent-strength curriculum (the untested lever)
    B  STANDARD_phase3b_entropy.yaml   same curriculum + 3x entropy bonus, no decay
    C  STANDARD_phase3c_selfplay.yaml  PPO self-play, --initialize-from run A

.EXAMPLE
  .\scripts\train-overnight.ps1
  .\scripts\train-overnight.ps1 -EndTime 06:00 -Only a,b
  .\scripts\train-overnight.ps1 -SkipEval -KeepUnity     # dry-ish run, editor untouched
#>
param(
    # Hard stop, local time. If it is already past this today, it means tomorrow.
    [string]$EndTime = "08:00",
    [string]$EnvPath = "Builds\PoSoccer\PoSoccer.exe",
    [int]$NumEnvs = 4,
    [int]$EvalEpisodes = 100,
    # Reserved per run for export + headless build + eval. Build is the slow part.
    [int]$WrapMinutes = 25,
    # A run shorter than this is not worth starting; the window is banked instead.
    [int]$MinRunMinutes = 20,
    [string[]]$Only,
    [switch]$SkipEval,
    # Leave the Unity editor alone. Implies eval is skipped for any run that
    # needs a rebuild, because build-headless.ps1 cannot run with the editor open.
    [switch]$KeepUnity,
    [string]$Venv
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$stamp = Get-Date -Format "yyyyMMdd"
$outDir = Join-Path $root "results\overnight\$stamp"
New-Item -ItemType Directory -Force $outDir | Out-Null

$transcript = Join-Path $outDir "orchestrator.log"
Start-Transcript -Path $transcript -Append | Out-Null

function Say([string]$m) { Write-Host ("[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $m) }

# Set-StrictMode makes a missing property a terminating error, and the eval JSON
# is written by Agent_EvalStats in C# - a schema change there would otherwise
# take down the whole night at 3am. Read every eval field through this.
function Get-Prop($obj, [string]$name) {
    if ($null -eq $obj) { return $null }
    $p = $obj.PSObject.Properties[$name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

# ---------------------------------------------------------------- deadline ---
$end = [datetime]::ParseExact($EndTime, "HH:mm", $null)
if ($end -le (Get-Date)) { $end = $end.AddDays(1) }
Say "Hard stop at $($end.ToString('yyyy-MM-dd HH:mm')) - $([int]($end - (Get-Date)).TotalMinutes) min available."

# ------------------------------------------------------------------- venv ----
# train-phase1.ps1's convention: .venv2 wins when it exists (Unity can lock
# .venv at runtime). Both are verified here because a venv with a half-installed
# protobuf imports fine until the trainer opens its gRPC channel and then dies
# minutes in - which overnight looks identical to a successful start.
function Test-Venv([string]$path) {
    $py = Join-Path $path "Scripts\python.exe"
    if (-not (Test-Path $py)) { return $false }
    & $py -c "import google.protobuf.internal.api_implementation, google.protobuf.internal.type_checkers; import mlagents.trainers; import mlagents_envs.communicator_objects" 2>&1 | Out-Null
    return ($LASTEXITCODE -eq 0)
}

$candidates = if ($Venv) { @($Venv) } else { @("$root\.venv2", "$root\.venv") }
$useVenv = $null
foreach ($c in $candidates) {
    if (Test-Venv $c) { $useVenv = $c; Say "Trainer venv: $c (import check passed)."; break }
    Say "Rejected venv $c - protobuf/mlagents import check failed."
}
if (-not $useVenv) {
    throw "No usable venv. Fix with: .venv\Scripts\pip install --force-reinstall --no-cache-dir protobuf==3.20.3"
}
$mlagents = Join-Path $useVenv "Scripts\mlagents-learn.exe"
if (-not (Test-Path $mlagents)) { throw "No mlagents-learn.exe in $useVenv - run scripts/setup-training-env.ps1." }

$envExe = Join-Path $root $EnvPath
if (-not (Test-Path $envExe)) { throw "No player build at $envExe - build SCN_Training first." }

# ------------------------------------------------------------ unity editor ---
# build-headless.ps1 drives Unity's CLI, which refuses to open a project another
# editor already has locked. Close politely, then force.
function Close-UnityEditor {
    # A batchmode Unity is somebody's build (scripts/build-android.ps1,
    # build-headless.ps1), not an interactive editor. Killing it destroys a
    # 20-minute IL2CPP build and leaves a half-written artifact, so wait it out
    # instead - up to 40 minutes, which is longer than any build here takes.
    $waited = 0
    while ($true) {
        $batch = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
                   Where-Object { $_.Name -eq "Unity.exe" -and $_.CommandLine -match "-batchmode" })
        if (-not $batch) { break }
        if ($waited -ge 2400) {
            Say "WARNING: a batchmode Unity build has been running 40 min; proceeding anyway."
            break
        }
        if ($waited -eq 0) { Say "A batchmode Unity build is running (PID $($batch[0].ProcessId)). Waiting for it rather than killing it." }
        Start-Sleep -Seconds 30
        $waited += 30
    }
    if ($waited -gt 0) { Say "Batchmode build finished after $([int]($waited/60)) min of waiting." }

    $procs = @(Get-Process Unity -ErrorAction SilentlyContinue)
    if (-not $procs) { return $true }
    Say "Closing $($procs.Count) Unity editor process(es) so the headless build can take the project lock."
    foreach ($p in $procs) { $null = $p.CloseMainWindow() }
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 2
        if (-not (Get-Process Unity -ErrorAction SilentlyContinue)) { Say "Editor closed cleanly."; return $true }
    }
    # A "save scene?" dialog blocks CloseMainWindow. Unsaved scene edits are lost
    # here - that is the documented cost of -KeepUnity being off.
    Say "WARNING: editor did not close in 60s (likely an unsaved-changes dialog). Forcing."
    Get-Process Unity -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 5
    return $true
}

# Resolve the editor matching ProjectVersion.txt. build-headless.ps1's default
# is hardcoded to 6000.5.4f1 while this project is on 6000.5.6f1, so never rely
# on it.
function Resolve-UnityExe {
    $verLine = Select-String -Path (Join-Path $root "ProjectSettings\ProjectVersion.txt") -Pattern '^m_EditorVersion:' | Select-Object -First 1
    $ver = ($verLine.Line -split ':', 2)[1].Trim()
    $exact = "C:\Program Files\Unity\Hub\Editor\$ver\Editor\Unity.exe"
    if (Test-Path $exact) { return $exact }
    Say "WARNING: no editor at $exact - falling back to the newest installed version."
    $any = Get-ChildItem "C:\Program Files\Unity\Hub\Editor" -Directory -ErrorAction SilentlyContinue |
           Sort-Object Name -Descending |
           ForEach-Object { Join-Path $_.FullName "Editor\Unity.exe" } |
           Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $any) { throw "No Unity editor found under C:\Program Files\Unity\Hub\Editor." }
    return $any
}

$doEval = -not $SkipEval -and -not $KeepUnity
if (-not $KeepUnity) { Close-UnityEditor | Out-Null }
elseif (-not $SkipEval) { Say "-KeepUnity set: eval disabled (headless build needs the project lock)." }

# ------------------------------------------------------------------ queue ----
$queue = @(
    [pscustomobject]@{ Key = "a"; Config = "STANDARD_phase3_opponent.yaml";  Port = 5010
                       Label = "opponent-strength curriculum";              InitFromKey = "" }
    [pscustomobject]@{ Key = "b"; Config = "STANDARD_phase3b_entropy.yaml";  Port = 5030
                       Label = "same curriculum + 3x entropy, no decay";     InitFromKey = "" }
    [pscustomobject]@{ Key = "c"; Config = "STANDARD_phase3c_selfplay.yaml"; Port = 5050
                       Label = "PPO self-play seeded from run A";            InitFromKey = "a" }
)
if ($Only) { $queue = $queue | Where-Object { $Only -contains $_.Key } }
foreach ($q in $queue) {
    $cfg = Join-Path $root "config\$($q.Config)"
    if (-not (Test-Path $cfg)) { throw "Missing config $cfg" }
}

$runIds = @{}
$report = @()
$env:POSOCCER_PROFILE = "STANDARD"

# ------------------------------------------------------------- per-run loop --
$idx = 0
foreach ($q in $queue) {
    $idx++
    $remainingRuns = $queue.Count - $idx + 1
    $minutesLeft = ($end - (Get-Date)).TotalMinutes
    $slice = [math]::Floor(($minutesLeft / $remainingRuns) - $WrapMinutes)

    if ($slice -lt $MinRunMinutes) {
        Say "SKIP run $($q.Key): only $([int]$minutesLeft) min left, slice would be $slice min."
        $report += [pscustomobject]@{ key = $q.Key; runId = $null; config = $q.Config; label = $q.Label
                                      status = "skipped_no_time"; sliceMinutes = $slice }
        continue
    }

    $runId = "p3$($q.Key)_$stamp"
    $runIds[$q.Key] = $runId
    $trainLog = Join-Path $outDir "$runId.train.log"
    $errLog = Join-Path $outDir "$runId.train.err.log"

    $mlArgs = @((Join-Path $root "config\$($q.Config)"),
                "--run-id=$runId", "--base-port=$($q.Port)",
                "--results-dir=$root\results", "--env=$envExe",
                "--no-graphics", "--num-envs=$NumEnvs", "--force")

    if ($q.InitFromKey) {
        $seed = $runIds[$q.InitFromKey]
        # Only seed from a run that actually produced weights, otherwise
        # mlagents-learn aborts on a missing checkpoint and burns the slice.
        if ($seed -and (Test-Path (Join-Path $root "results\$seed"))) {
            $mlArgs += "--initialize-from=$seed"
            Say "Run $($q.Key) seeds from $seed."
        } else {
            Say "WARNING: run $($q.Key) wanted --initialize-from run '$($q.InitFromKey)' but it produced nothing. Starting from scratch."
        }
    }

    Say "=== run $($q.Key) | $runId | $($q.Label) | slice $slice min ==="
    $started = Get-Date
    $p = Start-Process -FilePath $mlagents -ArgumentList $mlArgs -PassThru -NoNewWindow `
                       -RedirectStandardOutput $trainLog -RedirectStandardError $errLog
    $killAt = $started.AddMinutes($slice)
    $hitDeadline = $false
    while (-not $p.HasExited) {
        if ((Get-Date) -ge $killAt) { $hitDeadline = $true; break }
        Start-Sleep -Seconds 20
    }
    if ($hitDeadline) {
        Say "Run $($q.Key) hit its slice - stopping trainer, will export the newest checkpoint."
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 10
    }
    $trainExit = if ($hitDeadline) { $null } else { $p.ExitCode }
    & (Join-Path $PSScriptRoot "cleanup-training.ps1")
    $trainMinutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
    Say "Run $($q.Key) trained $trainMinutes min (exit $trainExit, deadline-stopped=$hitDeadline)."

    # ----------------------------------------------------- scrape the log ----
    # NB: the numeric groups must not be [\d.]+ - ML-Agents writes
    # "Mean Reward: 1.688. Std of Reward: ..." and the greedy class swallows the
    # sentence-ending period, producing "1.688." which is not parseable.
    $steps = $null; $meanReward = $null; $lesson = $null; $elo = $null
    $lessonTrail = @()
    if (Test-Path $trainLog) {
        $lines = Get-Content $trainLog
        $lastStep = $lines | Select-String -Pattern 'Step:\s*(\d+)' | Select-Object -Last 1
        if ($lastStep) { $steps = [int64]$lastStep.Matches[0].Groups[1].Value }
        $lastReward = $lines | Select-String -Pattern 'Mean Reward:\s*(-?\d+(?:\.\d+)?)' | Select-Object -Last 1
        if ($lastReward) { $meanReward = [double]$lastReward.Matches[0].Groups[1].Value }
        $lastElo = $lines | Select-String -Pattern 'ELO:\s*(\d+(?:\.\d+)?)' | Select-Object -Last 1
        if ($lastElo) { $elo = [double]$lastElo.Matches[0].Groups[1].Value }

        # Lesson pacing, not just the final lesson. A curriculum that clears all
        # four lessons inside the first couple of million steps is not a
        # curriculum - it means the advance thresholds sit below the reward the
        # policy already earns, which is the exact failure the phase-3 config was
        # written to fix. The morning review checks this.
        $stepAtLine = 0
        foreach ($line in $lines) {
            $m = [regex]::Match($line, 'Step:\s*(\d+)')
            if ($m.Success) { $stepAtLine = [int64]$m.Groups[1].Value; continue }
            $m = [regex]::Match($line, "Parameter '([^']+)' is in lesson '([^']+)' and has value '[^:]+:\s*value=(-?\d+(?:\.\d+)?)")
            if ($m.Success) {
                $lessonTrail += [pscustomobject]@{
                    atStep    = $stepAtLine
                    parameter = $m.Groups[1].Value
                    lesson    = $m.Groups[2].Value
                    value     = [double]$m.Groups[3].Value
                }
                $lesson = $m.Groups[2].Value
            }
        }
    }

    # ---------------------------------------------- export + promote model ---
    # A deadline-stopped trainer never writes <Behavior>.onnx at the run root -
    # only timestamped checkpoints. update-model.ps1 looks at the root only, so
    # promote the newest checkpoint into that name first (this is why
    # results/soccer_v3_00 has no root .onnx today).
    $runDir = Join-Path $root "results\$runId"
    $finalOnnx = Join-Path $runDir "STANDARD.onnx"
    $exportedFrom = "trainer"
    if (-not (Test-Path $finalOnnx)) {
        $ckpt = Get-ChildItem (Join-Path $runDir "STANDARD") -Filter "*.onnx" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($ckpt) {
            Copy-Item $ckpt.FullName $finalOnnx -Force
            $exportedFrom = "checkpoint:$($ckpt.Name)"
            Say "Exported $($ckpt.Name) as the run's final model."
        } else {
            $exportedFrom = "none"
            Say "WARNING: run $($q.Key) produced no .onnx at all."
        }
    }

    $promoted = $false
    if ($exportedFrom -ne "none") {
        try { & (Join-Path $PSScriptRoot "update-model.ps1") -RunId $runId -Profile STANDARD; $promoted = $true }
        catch { Say "WARNING: update-model failed for $runId - $($_.Exception.Message)" }
    }

    # ------------------------------------------------------ build + evaluate --
    $buildOk = $null; $evalExit = $null; $eval = $null
    if ($doEval -and $promoted) {
        $minutesLeft = ($end - (Get-Date)).TotalMinutes
        if ($minutesLeft -lt 8) {
            Say "Not enough time left to build+eval run $($q.Key); leaving it for the morning."
        } else {
            try {
                Say "Rebuilding headless player (stale build = grading the wrong weights)."
                & (Join-Path $PSScriptRoot "build-headless.ps1") -UnityExe (Resolve-UnityExe)
                $buildOk = $true
            } catch {
                $buildOk = $false
                Say "WARNING: headless build failed - $($_.Exception.Message)"
            }
            if ($buildOk) {
                # evaluate.ps1 sets its own $ErrorActionPreference = "Stop", so its
                # Write-Error paths (exit 2/3) surface here as terminating errors.
                # Catching keeps a bad eval from costing the runs still queued.
                try {
                    & (Join-Path $PSScriptRoot "evaluate.ps1") -RunId $runId -Episodes $EvalEpisodes -Profile STANDARD
                    $evalExit = $LASTEXITCODE   # 0 pass / 1 below bar / 2-3 setup error
                } catch {
                    $evalExit = -1
                    Say "WARNING: evaluate.ps1 threw for $runId - $($_.Exception.Message)"
                }
                $evalPath = Join-Path $root "results\eval\$runId.json"
                if (Test-Path $evalPath) {
                    $eval = Get-Content $evalPath -Raw | ConvertFrom-Json
                    Say ("Eval {0}: {1}/{2} blue wins, {3} stalemates." -f $runId,
                         (Get-Prop $eval 'blueWins'), (Get-Prop $eval 'episodes'), (Get-Prop $eval 'stalemates'))
                }
            }
        }
    }

    $ep = Get-Prop $eval 'episodes'
    $bw = Get-Prop $eval 'blueWins'
    $rw = Get-Prop $eval 'redWins'
    $sm = Get-Prop $eval 'stalemates'
    # Guard the numerators too: $null / 100 is 0 in PowerShell, which would
    # report a missing field as a genuine 0% rather than as unknown.
    $winRate = if ($ep -and $null -ne $bw) { [math]::Round($bw / $ep, 4) } else { $null }
    $staleRate = if ($ep -and $null -ne $sm) { [math]::Round($sm / $ep, 4) } else { $null }

    $report += [pscustomobject]@{
        key = $q.Key; runId = $runId; config = $q.Config; label = $q.Label
        status = if ($exportedFrom -eq "none") { "no_model" } elseif ($hitDeadline) { "deadline_stopped" } else { "completed" }
        sliceMinutes = $slice; trainMinutes = $trainMinutes; trainExit = $trainExit
        steps = $steps; meanReward = $meanReward; lastLesson = $lesson; elo = $elo
        lessonTrail = $lessonTrail
        exportedFrom = $exportedFrom; promoted = $promoted; buildOk = $buildOk
        evalExit = $evalExit; episodes = $ep; blueWins = $bw; redWins = $rw; stalemates = $sm
        winRate = $winRate; stalemateRate = $staleRate
        meanEpisodeSteps = (Get-Prop $eval 'meanEpisodeSteps')
        evalInvalid = (Get-Prop $eval 'invalid')
        trainLog = $trainLog
    }

    # Write after every run, not just at the end: if the box reboots at 04:00 the
    # morning review still has whatever finished before that.
    $summary = [pscustomobject]@{
        date = $stamp; startedBefore = $end.ToString("s"); venv = $useVenv
        numEnvs = $NumEnvs; evalEpisodes = $EvalEpisodes
        baselineNote = "bot-vs-bot baseline is 42.5% wins / 15% stalemates; acceptance bar is >=80% wins and <=10% stalemates"
        priorBest = "soccer_v3_00 = 18% wins / 18% stalemates at ~15.7M steps"
        runs = $report
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $outDir "summary.json") -Encoding UTF8
}

Remove-Item Env:\POSOCCER_PROFILE -ErrorAction SilentlyContinue
& (Join-Path $PSScriptRoot "cleanup-training.ps1")

Say "Done. Summary: $outDir\summary.json"
$report | Format-Table key, runId, status, steps, meanReward, lastLesson, winRate, stalemateRate -AutoSize
Stop-Transcript | Out-Null
