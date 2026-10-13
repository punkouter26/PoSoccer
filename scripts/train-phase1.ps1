# Phase 1 PPO bootstrap training vs the heuristic bot.
# In-editor:  .\scripts\train-phase1.ps1 -RunId soccer_p1_00      (press Play when prompted)
# Headless:   .\scripts\train-phase1.ps1 -RunId soccer_p1_00 -EnvPath Builds\PoSoccer\PoSoccer.exe -NumEnvs 4
# Team (2v2): .\scripts\train-phase1.ps1 -RunId soccer_p30team_standard -Config TRAIN_STANDARD_p30team.yaml -Squad "1,2" -EnvPath ...
param(
    [string]$RunId = "soccer_p1_00",
    [string]$EnvPath = "",
    [int]$NumEnvs = 4,
    [int]$BasePort = 5005,
    [string]$Config = "TRAIN_STANDARD_p30team.yaml",
    [string]$InitFrom = "",
    [switch]$Resume,
    [switch]$Force,
    [Parameter(HelpMessage = "Leave both teams on the trainer (symmetric self-play) instead of facing the bot.")]
    [switch]$SelfPlay,
    [Parameter(HelpMessage = "League: the pitch grid mixes self-play pitches with pitches where one side is the scripted bot. Needs a config with a self_play block.")]
    [switch]$League,
    [Parameter(HelpMessage = "Scripted-bot version the learner faces (1 = benchmark bot, 2 = lead pursuit and bank shots).")]
    [ValidateRange(1, 2)]
    [int]$BotVersion = 1,
    [Parameter(HelpMessage = "Players per side on each training pitch, cycled over the grid: '2' = all 2v2, '1,2' = alternate. Empty = the authored 1v1.")]
    [string]$Squad = ""
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
if ($SelfPlay -and $League) { throw "-SelfPlay and -League are different opponent pools; pass one." }
$env:POSOCCER_OPPONENT = if ($League) { "league" } elseif ($SelfPlay) { "" } else { "bot" }
# In a league run the bot sits on either colour, so both sides get the same version.
# Confirm the seating reached the player on TensorBoard: PoSoccer/league_bot_pitch (about 0.5).
$env:POSOCCER_BOT_VERSION = "$BotVersion"
$env:POSOCCER_BOT_VERSION_BLUE = "$BotVersion"

# Squad size per pitch (Agent_EnvController.ApplyTrainingSquad). Validated here so a
# typo fails before a multi-hour run rather than as one LogError in a player log.
# Confirm it reached the player on TensorBoard: PoSoccer/blue_squad (1.5 for "1,2").
if ($Squad -and $Squad -notmatch '^\s*\d+\s*(,\s*\d+\s*)*$') {
    throw "-Squad '$Squad' must be a comma-separated list of squad sizes, e.g. '2' or '1,2'."
}
$env:POSOCCER_SQUAD = $Squad

# The default used to be STANDARD_phase1_ppo.yaml, which lived in config\_archive
# and therefore never resolved - the documented default invocation of this script
# could not start a run. Resolve and verify before handing the path to the trainer,
# so a missing config fails here, naming the file, rather than inside mlagents-learn.
$configPath = Join-Path "$root\config" $Config
if (-not (Test-Path $configPath)) {
    $available = (Get-ChildItem "$root\config\*.yaml" | ForEach-Object { $_.Name }) -join ", "
    throw "Config not found: $configPath`nAvailable in config\: $available"
}

$mlArgs = @($configPath,
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
    $env:POSOCCER_SQUAD = ""
    $env:POSOCCER_BOT_VERSION = ""
    $env:POSOCCER_BOT_VERSION_BLUE = ""
    # Lifecycle guardrail: no orphaned trainer/env processes after a run.
    & "$root\scripts\cleanup-training.ps1"
    # Auto-assign the freshest checkpoint into the agent prefab slot (GUID preserved).
    & "$root\scripts\update-model.ps1" -RunId $RunId
}
