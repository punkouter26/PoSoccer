# Observability (UNITY_RULES): restart TensorBoard, cleaning unused items first.
# "Unused" = result dirs with no checkpoints/events, plus any already-running TB session.
param([int]$Port = 6006)

$ErrorActionPreference = "SilentlyContinue"
$root = Split-Path $PSScriptRoot -Parent
$results = Join-Path $root "results"

# 1. Kill existing TensorBoard sessions (always restart clean).
# Match the tensorboard EXECUTABLE only - never this script's own shell
# (whose command line also contains the word "tensorboard").
Get-CimInstance Win32_Process |
    Where-Object { $_.ProcessId -ne $PID -and
        ($_.Name -match "^tensorboard" -or
         ($_.Name -match "^python" -and $_.CommandLine -match "tensorboard")) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -Confirm:$false }

# 2. Prune stale runs: no .onnx export, no checkpoint, or no event files -> dead weight.
if (Test-Path $results) {
    Get-ChildItem $results -Directory | Where-Object { $_.Name -ne "eval" } | ForEach-Object {
        $hasEvents = Get-ChildItem $_.FullName -Recurse -Filter "events.out.tfevents.*" | Select-Object -First 1
        $hasModel = Get-ChildItem $_.FullName -Recurse -Include "*.onnx", "*.pt" | Select-Object -First 1
        if (-not $hasEvents -and -not $hasModel) {
            Write-Host "Pruning stale run: $($_.Name)"
            Remove-Item $_.FullName -Recurse -Force
        }
    }
}

# 3. Relaunch (venv exe called directly - PATH-independent, works detached).
$ErrorActionPreference = "Stop"
Write-Host "TensorBoard -> http://localhost:$Port (reward convergence / policy entropy / value loss)"
& "$root\.venv\Scripts\tensorboard.exe" --logdir $results --port $Port --bind_all
