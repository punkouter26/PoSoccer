# Record the SCRIPTED BOT playing, into a .demo file that behavioral cloning / GAIL
# can train against.
#
# WHY THIS EXISTS (2026-09-07). The acceptance bar is >=80% wins, but the honest
# near-term target is BOT PARITY: bot-vs-bot measures 42.5%, and the best brain this
# project has produced grades 26.6% - still 16 points WORSE than the opponent it is
# trying to beat. Seven reward/curriculum/perception levers have been tried and
# exactly one (p21's curriculum, +9.1 pp) cleared the +/-4.7 pp detection threshold
# at n=350. Another coefficient will not close 53 points.
#
# The bot is an ideal demonstrator and has never been used as one: same observation
# and action space, same 12.5 Hz decision rate (both agents carry DecisionRequester
# period 8, verified), unlimited episodes, in-process. `behavioral_cloning` has been
# `None` in every config this project has run, while the pinned trainer has supported
# it all along.
#
#   .\scripts\record-demo.ps1 [-Steps 200000] [-OutDir Assets\Demonstrations]
param(
    [int]$Steps = 150000,
    [string]$OutDir = "Assets\Demonstrations",
    [string]$ExePath = "Builds\PoSoccer\PoSoccer.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$exe  = Join-Path $root $ExePath
if (-not (Test-Path $exe)) { Write-Error "No player at $exe - build first."; exit 2 }

$outFull = Join-Path $root $OutDir
New-Item -ItemType Directory -Force $outFull | Out-Null

# No trainer is connected, so Agent_TrainingGrid keeps ONE pitch (it only clones when
# a communicator is on or eval mode is set) - which is what we want: 16 pitches would
# spawn 16 recorders and 16 files.
#
# ApplyDemoRecording forces BOTH sides to HeuristicOnly, so the demonstrations come
# from BOT-VS-BOT - the same distribution behind the 42.5% symmetric baseline. That
# forcing is required, not incidental: SCN_Training serializes a brainModel on both
# agents, so otherwise BLUE would run the trained policy under inference and the demos
# would be "bot beating a weak opponent" - a one-sided distribution missing exactly
# the contested states a learner needs to imitate.
$env:POSOCCER_OPPONENT    = "bot"
$env:POSOCCER_RECORD_DEMO = "1"
$env:POSOCCER_DEMO_STEPS  = "$Steps"
$env:POSOCCER_DEMO_DIR    = $outFull

Write-Host "Recording $Steps steps of bot play -> $outFull"
Write-Host "  (the recorder calls Application.Quit itself once the step count is hit)"

try {
    $before = @(Get-ChildItem $outFull -Filter *.demo -ErrorAction SilentlyContinue).Count
    # -logFile - streams to stdout; the player exits on its own via NumStepsToRecord.
    & $exe -batchmode -nographics -logFile - | Out-Null
    $demos = @(Get-ChildItem $outFull -Filter *.demo -ErrorAction SilentlyContinue)
    if ($demos.Count -le $before) {
        Write-Error "No .demo file was produced. Check that the player was rebuilt AFTER the ApplyDemoRecording change - the env var is inert in an older build."
        exit 1
    }
    $newest = $demos | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    "{0}  ({1:N0} bytes)" -f $newest.FullName, $newest.Length | Write-Host
    Write-Host "Point behavioral_cloning.demo_path at it, then train."
}
finally {
    $env:POSOCCER_RECORD_DEMO = ""
    $env:POSOCCER_DEMO_STEPS  = ""
    $env:POSOCCER_DEMO_DIR    = ""
    $env:POSOCCER_OPPONENT    = ""
    & "$root\scripts\cleanup-training.ps1"
}
