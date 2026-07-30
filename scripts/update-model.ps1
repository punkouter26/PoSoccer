# Model pipeline (UNITY_RULES): overwrite the tracked .onnx IN PLACE so the .meta
# GUID reference on the agent prefab never changes; Unity hot-reloads the weights.
param([Parameter(Mandatory = $true)][string]$RunId)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root "results\$RunId\SoccerAgent.onnx"
$target = Join-Path $root "Assets\Agents\SoccerAgent_v01\SoccerAgent_v01.onnx"

if (-not (Test-Path $source)) {
    Write-Warning "No exported model at $source - nothing to assign."
    exit 0
}

Copy-Item $source $target -Force
Write-Host "OK: $RunId -> Assets/Agents/SoccerAgent_v01/SoccerAgent_v01.onnx (GUID preserved)"
