# Model pipeline (UNITY_RULES): overwrite the tracked .onnx IN PLACE so the .meta
# GUID reference on the agent prefab / Reward_ profile never changes; Unity
# hot-reloads the weights. One GUID-stable slot per personality.
#
#   .\scripts\update-model.ps1 -RunId soccer_p2_00                  # -> STANDARD
#   .\scripts\update-model.ps1 -RunId matt_p1_00 -Profile MATT      # -> MATT
param(
    [Parameter(Mandatory = $true)][string]$RunId,
    [ValidateSet("STANDARD", "MATT", "KIM", "NICK")]
    [string]$Profile = "STANDARD",
    [string]$Behavior
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot\lib-profile.ps1"

# Agent asset folders follow <AgentName>_v<NN> (UNITY_RULES 1).
$folders = @{
    STANDARD = "Standard_v01"
    MATT     = "Matt_v01"
    KIM      = "Kim_v01"
    NICK     = "Nick_v01"
}
$folder = $folders[$Profile]

# The trainer exports under the behavior name, which defaults to the profile name.
if (-not $Behavior) { $Behavior = $Profile }

$source = Join-Path $root "results\$RunId\$Behavior.onnx"
# Legacy runs (pre-rename) exported under the old behavior name.
if (-not (Test-Path $source)) {
    $legacy = Join-Path $root "results\$RunId\SoccerAgent.onnx"
    if (Test-Path $legacy) { $source = $legacy }
}

if (-not (Test-Path $source)) {
    Write-Warning "No exported model at $source - nothing to assign."
    exit 0
}

$targetDir = Join-Path $root "Assets\Agents\$folder"
if (-not (Test-Path $targetDir)) {
    throw "Agent folder missing: Assets/Agents/$folder"
}
$target = Join-Path $targetDir "$Profile.onnx"

$isNewSlot = -not (Test-Path $target)
Copy-Item $source $target -Force

if ($isNewSlot) {
    Write-Host "NEW slot created: Assets/Agents/$folder/$Profile.onnx"
    Write-Host "  One-time step: open Assets/Agents/$folder/Reward_$Profile.asset in Unity"
    Write-Host "  and drag $Profile.onnx into its 'brainModel' field. Every later run"
    Write-Host "  overwrites the file in place, so the GUID never changes again."
} else {
    Write-Host "OK: $RunId -> Assets/Agents/$folder/$Profile.onnx (GUID preserved)"
}

# Surface profiles that are still running as heuristic bots.
$profileAsset = Join-Path $targetDir "Reward_$Profile.asset"
if ((Test-Path $profileAsset) -and -not (Select-String -Path $profileAsset -Pattern 'brainModel:' -Quiet)) {
    Write-Warning "Reward_$Profile.asset has no brainModel yet - $Profile still plays as a heuristic bot."
}

# Stamp training provenance onto the profile so the menu can show what is behind
# each brain. Steps come from the checkpoint filename (<BEHAVIOR>-<steps>.onnx);
# the run root export has no step in its name, so fall back to the highest
# numbered checkpoint in the run.
$steps = 0
$checkpointDir = Join-Path $root "results\$RunId\$Behavior"
if (Test-Path $checkpointDir) {
    $steps = Get-ChildItem $checkpointDir -Filter "$Behavior-*.onnx" |
        ForEach-Object { [int]($_.BaseName -replace '^.*-', '') } |
        Sort-Object -Descending | Select-Object -First 1
}
if (-not $steps) { $steps = 0 }

# Deploying new weights invalidates any previously measured win rate; evaluate.ps1
# writes a fresh one. -1 renders as "unrated" in the menu.
Set-ProfileField $profileAsset 'trainingSteps' "$steps"
Set-ProfileField $profileAsset 'trainingRunId' $RunId
Set-ProfileField $profileAsset 'trainedOn' (Get-Date -Format 'yyyy-MM-dd')
Set-ProfileField $profileAsset 'evalWinRate' '-1'
Set-ProfileField $profileAsset 'evalEpisodes' '0'

# ActionGain provenance. Read from the SOURCE rather than hardcoded here, because a
# constant duplicated into a deploy script is exactly how the value silently diverges
# from the runtime it is supposed to describe.
#
# This exists because on 2026-09-07 the gain went 1.6 -> 1.0 (p22) while MATT, NICK and
# KIM stayed on p21 checkpoints trained at 1.6. Tensor shapes are unchanged, so those
# .onnx load without a warning and then under-drive. Agent_EditMode_ActionGain compares
# what this stamps against Agent_Soccer.ActionGain and fails on a mismatch.
$agentSource = Join-Path $root "Assets\Scripts\Agents\Agent_Soccer.cs"
$gain = $null
if (Test-Path $agentSource) {
    $m = Select-String -Path $agentSource -Pattern 'public\s+const\s+float\s+ActionGain\s*=\s*([0-9.]+)f' |
         Select-Object -First 1
    if ($m) { $gain = $m.Matches[0].Groups[1].Value }
}
if ($gain) {
    Set-ProfileField $profileAsset 'trainedActionGain' $gain
} else {
    Write-Warning "Could not read Agent_Soccer.ActionGain from source - trainedActionGain NOT stamped. Agent_EditMode_ActionGain will flag this profile as unknown provenance."
}

Write-Host "  provenance: $steps steps from $RunId, ActionGain $gain (eval rating cleared)"
