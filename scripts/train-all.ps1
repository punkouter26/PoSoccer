# Trains every personality brain in sequence against the scripted bot.
#
# POSOCCER_PROFILE is read in Agent_EnvController.Awake and swaps both agents
# onto that profile (and its behavior name) before the brain contract is frozen,
# so one build trains any brain without a scene edit per run.
#
#   .\scripts\train-all.ps1                              # all four, sequentially
#   .\scripts\train-all.ps1 -Profiles MATT,KIM,NICK      # just the personalities
#
# Personality lives ONLY in the reward DNA of Assets/Agents/*/Reward_<NAME>.asset.
# The four TRAIN_<NAME>_v3.yaml configs are byte-identical apart from the behavior
# name, so any behavioural difference between the finished brains is reward DNA
# rather than trainer tuning.
param(
    [string[]]$Profiles = @("STANDARD", "MATT", "KIM", "NICK"),
    [string]$EnvPath = "Builds\PoSoccer\PoSoccer.exe",
    [int]$NumEnvs = 4,
    [Parameter(HelpMessage = "Run-id suffix: soccer_<Tag>_<profile>.")]
    [string]$Tag = "v3",
    [Parameter(HelpMessage = "Selects config\TRAIN_<NAME>_<ConfigTag>.yaml.")]
    [string]$ConfigTag = "v3",
    [Parameter(HelpMessage = "Leave both teams on the trainer (symmetric self-play) instead of facing the bot.")]
    [switch]$SelfPlay,
    [Parameter(HelpMessage = "Train against a build older than the runtime sources (normally fatal).")]
    [switch]$AllowStale,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# Prefer .venv2 when present - the canonical .venv can be locked by Unity at
# runtime. Matches train-phase1.ps1 rather than hardcoding .venv.
$venv = if (Test-Path "$root\.venv2\Scripts\mlagents-learn.exe") { "$root\.venv2" } else { "$root\.venv" }
$py = Join-Path $venv "Scripts\mlagents-learn.exe"
if (-not (Test-Path $py)) { throw "No trainer at $py - run scripts/setup-training-env.ps1 first." }

# NB: not named $env - that collides with PowerShell's environment provider,
# which this script uses below to pass POSOCCER_PROFILE to the player.
$envExe = Join-Path $root $EnvPath
if (-not (Test-Path $envExe)) { throw "No player build at $envExe - build SCN_Training first." }

# Stale-build guard. Phases 9 and 10 were all trained and graded against a player
# three weeks older than the code under test, and nothing caught it. Judge the
# build by PoSoccer_Data - Unity leaves the .exe mtime untouched on a successful
# rebuild, so the .exe is not evidence of anything.
$dataDir = Join-Path (Split-Path $envExe -Parent) "PoSoccer_Data"
if (Test-Path $dataDir) {
    $builtUtc = (Get-ChildItem $dataDir -Filter *.assets |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
    $srcUtc = (Get-ChildItem (Join-Path $root "Assets\Scripts") -Recurse -Filter *.cs |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
    $assetUtc = (Get-ChildItem (Join-Path $root "Assets\Agents") -Recurse -Filter Reward_*.asset |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
    $newestInput = if ($assetUtc -gt $srcUtc) { $assetUtc } else { $srcUtc }
    if ($builtUtc -lt $newestInput) {
        $msg = "STALE BUILD: player data is $builtUtc UTC but runtime sources/reward assets are $newestInput UTC. " +
               "Rebuild via MCP manage_build before training, or pass -AllowStale to override. " +
               "COMMON CAUSE: evaluate.ps1 stamps evalWinRate/evalEpisodes/trainingRunId onto " +
               "Reward_<PROFILE>.asset when it finishes, so any training run launched after an eval " +
               "trips this. That stamp is provenance only and cannot change behaviour, but mtime " +
               "cannot tell it apart from a real reward-DNA edit - so rebuild rather than override, " +
               "because the one time it is not provenance is the time it costs you three phases."
        if ($AllowStale) { Write-Warning $msg } else { throw $msg }
    }
    Write-Host "build check OK: player data $builtUtc UTC >= newest input $newestInput UTC"
}

# Observability is not optional (UNITY_RULES): a run with no live curves cannot be
# told apart from a stalled one.
try {
    $null = Invoke-WebRequest -Uri "http://localhost:6006/" -UseBasicParsing -TimeoutSec 2
    Write-Host "TensorBoard reachable on :6006"
} catch {
    Write-Warning "TensorBoard is not reachable on :6006. Run .\scripts\tensorboard.ps1 in another terminal. Training will continue."
}

# "Train against the scripted bot" is opt-in, not the default. The training scene
# leaves both teams on BehaviorType.Default, which routes everything to the
# trainer, so without this every run is symmetric self-play, Agent_HeuristicBot
# never executes, and the bot_strength curriculum has nothing to act on - which is
# what silently happened to every run before 2026-08-04. Agent_Soccer's
# ApplyTrainingOpponent reads this and forces Red to HeuristicOnly. Env players are
# spawned as children of mlagents-learn, so they inherit it.
$env:POSOCCER_OPPONENT = if ($SelfPlay) { "" } else { "bot" }
Write-Host ("opponent: " + $(if ($SelfPlay) { "SELF-PLAY (both teams on the trainer)" } else { "scripted bot (POSOCCER_OPPONENT=bot)" }))

try {
    $basePort = 5010
    foreach ($name in $Profiles) {
        $runId = "soccer_${Tag}_$($name.ToLower())"
        $config = Join-Path $root "config\TRAIN_${name}_${ConfigTag}.yaml"
        if (-not (Test-Path $config)) { throw "Missing config $config" }

        Write-Host ""
        Write-Host "=== training $name  (run-id $runId, port $basePort, config TRAIN_${name}_${ConfigTag}.yaml) ==="

        # Inherited by the player processes mlagents-learn spawns.
        $env:POSOCCER_PROFILE = $name

        $mlArgs = @($config, "--run-id=$runId", "--base-port=$basePort",
                    "--results-dir=$root\results", "--env=$envExe",
                    "--no-graphics", "--num-envs=$NumEnvs")
        if ($Force) { $mlArgs += "--force" }

        & $py @mlArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "$name exited with code $LASTEXITCODE - continuing to the next profile."
        }

        # No orphaned trainer/env players between runs.
        & (Join-Path $PSScriptRoot "cleanup-training.ps1")

        # Promote straight into the personality's GUID-stable slot. NOTE: this
        # copies the .onnx and stamps provenance, but on a NEW slot it cannot wire
        # Reward_<NAME>.brainModel - that reference has to be set in Unity, and
        # until it is, the personality still plays as a heuristic bot.
        & (Join-Path $PSScriptRoot "update-model.ps1") -RunId $runId -Profile $name

        $basePort += 20   # keep concurrent/leftover workers from colliding
    }
}
finally {
    Remove-Item Env:\POSOCCER_PROFILE -ErrorAction SilentlyContinue
    $env:POSOCCER_OPPONENT = ""
    & (Join-Path $PSScriptRoot "cleanup-training.ps1")
}

Write-Host ""
Write-Host "All requested profiles finished. Inspect with: .\scripts\tensorboard.ps1"
Write-Host "Then wire brainModel on any NEW slot before the personality stops playing as a bot."
